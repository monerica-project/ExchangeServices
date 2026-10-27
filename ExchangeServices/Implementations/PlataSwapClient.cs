using System.Globalization;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class PlataSwapOptions
{
    public string BaseUrl { get; set; } = "https://api.plataswap.com/meta/changer/";
    public string SiteName { get; set; } = "PlataSwap";
    public string? SiteUrl { get; set; } = "https://plataswap.com";

    /// <summary>Partner key from the PlataSwap partner dashboard (x-api-key). Optional: quotes work without it today.</summary>
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; } = 'B';
    public decimal MinAmountUsd { get; set; } = 40m;
}

/// <summary>
/// PlataSwap (https://plataswap.com). GET quote?from=xmr&amp;to=btc&amp;amount=4 →
/// { ok, receive, minAmount, maxAmount } (out-of-range → ok:false). Coin codes: xmr, btc, eth, usdttrc20, usdterc20.
/// </summary>
public sealed class PlataSwapClient : IPlataSwapClient
{
    private readonly HttpClient http;
    private readonly PlataSwapOptions opt;

    public PlataSwapClient(HttpClient http, IOptions<PlataSwapOptions> options)
    {
        this.http = http;
        opt = options.Value;
    }

    public string ExchangeKey => "plataswap";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Float;

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = query.ProbeAmount is decimal p && p > 0 ? ProbeSizing.Round(p) : 1m;
        var received = await ReceiveAsync(query.Base, query.Quote, probe, ct);
        return received is > 0m ? Result(query, received.Value / probe) : null;
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = ProbeSizing.BuyProbe(query, 2500m);
        var received = await ReceiveAsync(query.Quote, query.Base, probe, ct);
        return received is > 0m ? Result(query, probe / received.Value) : null;
    }

    internal static string? Code(AssetRef a)
    {
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        var n = (a.Network ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "XMR" or "BTC" or "ETH" or "LTC" => t.ToLowerInvariant(),
            "USDT" => n is "ethereum" or "erc20" or "eth" ? "usdterc20" : "usdttrc20",
            _ => null,
        };
    }

    private async Task<decimal?> ReceiveAsync(AssetRef from, AssetRef to, decimal amount, CancellationToken ct)
    {
        var f = Code(from);
        var t = Code(to);
        if (f is null || t is null || f == t) return null;

        var url = $"quote?from={f}&to={t}&amount={amount.ToString("0.########", CultureInfo.InvariantCulture)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(opt.ApiKey)) req.Headers.TryAddWithoutValidation("x-api-key", opt.ApiKey);

        var body = await HttpJson.SendAsync(http, req, opt.TimeoutSeconds, ct);
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
            return HttpJson.Dec(root, "receive");
        }
        catch { return null; }
    }

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}
