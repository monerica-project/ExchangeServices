using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class IronSwapsOptions
{
    public string BaseUrl { get; set; } = "https://ironswaps.org/api/";
    public string SiteName { get; set; } = "IronSwaps";
    public string? SiteUrl { get; set; } = "https://ironswaps.org";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; } = 'B';
    public decimal MinAmountUsd { get; set; } = 0m;
}

/// <summary>
/// IronSwaps (https://ironswaps.org), keyless. Uses the same quote call as the site's own swap form
/// (not a documented partner API): POST quote { from, to, amountFrom } →
/// { quote: { amountFrom, amountUserReceives, min, max } } (min/max 0 = no limit). Coin ids from GET currencies.
/// </summary>
public sealed class IronSwapsClient : IIronSwapsClient
{
    private readonly HttpClient http;
    private readonly IronSwapsOptions opt;

    public IronSwapsClient(HttpClient http, IOptions<IronSwapsOptions> options)
    {
        this.http = http;
        opt = options.Value;
    }

    public string ExchangeKey => "ironswaps";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Float;

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = query.ProbeAmount is decimal p && p > 0 ? ProbeSizing.Round(p) : 1m;
        var q = await QuoteAsync(query.Base, query.Quote, probe, ct);
        return q is null ? null : Result(query, q.Value.Out / q.Value.In);
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = ProbeSizing.BuyProbe(query, 2500m);
        var q = await QuoteAsync(query.Quote, query.Base, probe, ct);
        return q is null ? null : Result(query, q.Value.In / q.Value.Out);
    }

    internal static string? Code(AssetRef a)
    {
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        var n = (a.Network ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "XMR" => "xmr_mainnet",
            "BTC" => "btc_mainnet",
            "ETH" => "eth_mainnet",
            "USDT" => n is "ethereum" or "erc20" or "eth" ? "usdt_eth" : n is "solana" or "sol" ? "usdt_sol" : "usdt_trx",
            _ => null,
        };
    }

    private async Task<(decimal In, decimal Out)?> QuoteAsync(AssetRef from, AssetRef to, decimal amount, CancellationToken ct)
    {
        var f = Code(from);
        var t = Code(to);
        if (f is null || t is null || f == t) return null;

        var body = await HttpJson.PostJsonAsync(http, "quote", new { from = f, to = t, amountFrom = amount }, opt.TimeoutSeconds, ct);
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("quote", out var q)) return null;
            var amountIn = HttpJson.Dec(q, "amountFrom") ?? amount;
            var amountOut = HttpJson.Dec(q, "amountUserReceives") ?? HttpJson.Dec(q, "amountTo");
            var min = HttpJson.Dec(q, "min");
            var max = HttpJson.Dec(q, "max");
            if (amountIn <= 0m || amountOut is not > 0m) return null;
            if (min is > 0m && amountIn < min) return null;
            if (max is > 0m && amountIn > max) return null;
            return (amountIn, amountOut.Value);
        }
        catch { return null; }
    }

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}
