using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class BetKarmaOptions
{
    public string BaseUrl { get; set; } = "https://betkarma.art/api/exchange/public/";
    public string SiteName { get; set; } = "BetKarma";
    public string? SiteUrl { get; set; } = "https://betkarma.art";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; } = 'A';
    public decimal MinAmountUsd { get; set; } = 200m;
}

/// <summary>
/// BetKarma (https://betkarma.art) public exchange API, keyless, fixed rate.
/// GET estimate?pair=xmr/btc → { estimate: { outPerUnit, minAmount, maxAmount } }: outPerUnit is the
/// net amount received per 1 unit sent (service fee already taken off), limits are in the sent coin.
/// Uses the read-only GET estimate (not POST rate, which saves a quote record).
/// </summary>
public sealed class BetKarmaClient : IBetKarmaClient
{
    private readonly HttpClient http;
    private readonly BetKarmaOptions opt;

    public BetKarmaClient(HttpClient http, IOptions<BetKarmaOptions> options)
    {
        this.http = http;
        opt = options.Value;
    }

    public string ExchangeKey => "betkarma";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Fixed;

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = query.ProbeAmount is decimal p && p > 0 ? p : 1m;
        var perUnit = await OutPerUnitAsync(query.Base, query.Quote, probe, ct);
        return perUnit is > 0m ? Result(query, perUnit.Value) : null;
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = ProbeSizing.BuyProbe(query, 2500m);
        var perUnit = await OutPerUnitAsync(query.Quote, query.Base, probe, ct); // XMR per quote unit
        return perUnit is > 0m ? Result(query, 1m / perUnit.Value) : null;
    }

    internal static string? Code(AssetRef a)
    {
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        var n = (a.Network ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "XMR" or "BTC" or "ETH" or "LTC" => t.ToLowerInvariant(),
            "USDT" => n is "ethereum" or "erc20" or "eth" ? "usdt_erc20" : n is "solana" or "sol" ? "usdt_solana" : "usdt_tron",
            _ => null,
        };
    }

    private async Task<decimal?> OutPerUnitAsync(AssetRef from, AssetRef to, decimal amount, CancellationToken ct)
    {
        var f = Code(from);
        var t = Code(to);
        if (f is null || t is null || f == t) return null;

        var body = await HttpJson.GetAsync(http, $"estimate?pair={f}/{t}", opt.TimeoutSeconds, ct);
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) return null;
            if (!root.TryGetProperty("estimate", out var e)) return null;
            var perUnit = HttpJson.Dec(e, "outPerUnit");
            var min = HttpJson.Dec(e, "minAmount");
            var max = HttpJson.Dec(e, "maxAmount");
            if (perUnit is not > 0m) return null;
            if (min is > 0m && amount < min) return null;
            if (max is > 0m && amount > max) return null;
            return perUnit;
        }
        catch { return null; }
    }

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}
