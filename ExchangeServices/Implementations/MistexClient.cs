using System.Globalization;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class MistexOptions
{
    public string BaseUrl { get; set; } = "https://mistex.io/api/v1/";
    public string SiteName { get; set; } = "Mistex";
    public string? SiteUrl { get; set; } = "https://mistex.io";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; } = 'B';
    public decimal MinAmountUsd { get; set; } = 60m;
}

/// <summary>
/// Mistex (https://mistex.io) public partner rates API, no key needed (60 req/min keyless).
/// GET partner/rates?coin_from&amp;coin_to&amp;network_from&amp;network_to&amp;amount&amp;rate_type=float|fixed
/// → { rate, min_amount, max_amount } where received = amount × rate and the limits are in coin_from.
/// </summary>
public sealed class MistexClient : IMistexClient
{
    private readonly HttpClient http;
    private readonly MistexOptions opt;

    public MistexClient(HttpClient http, IOptions<MistexOptions> options)
    {
        this.http = http;
        opt = options.Value;
    }

    public string ExchangeKey => "mistex";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Float;

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = query.ProbeAmount is decimal p && p > 0 ? ProbeSizing.Round(p) : 1m;
        var rate = await RateAsync(query.Base, query.Quote, probe, query.Fixed, ct);
        return rate is > 0m ? Result(query, rate.Value) : null;
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = ProbeSizing.BuyProbe(query, 2500m);
        var rate = await RateAsync(query.Quote, query.Base, probe, query.Fixed, ct); // XMR per quote unit
        return rate is > 0m ? Result(query, 1m / rate.Value) : null;
    }

    internal static (string Coin, string Network) Code(AssetRef a)
    {
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        if (t is "USDT" or "USDC")
        {
            var n = (a.Network ?? "").Trim().ToLowerInvariant();
            return (t, n is "ethereum" or "erc20" or "eth" ? "ERC20" : "TRC20");
        }
        return (t, t);
    }

    private async Task<decimal?> RateAsync(AssetRef from, AssetRef to, decimal amount, bool fixedRate, CancellationToken ct)
    {
        var (fc, fn) = Code(from);
        var (tc, tn) = Code(to);
        if (fc.Length == 0 || tc.Length == 0 || fc == tc) return null;

        var url = "partner/rates" +
                  $"?coin_from={fc}&coin_to={tc}&network_from={fn}&network_to={tn}" +
                  $"&amount={amount.ToString("0.########", CultureInfo.InvariantCulture)}" +
                  $"&rate_type={(fixedRate ? "fixed" : "float")}";

        var body = await HttpJson.GetAsync(http, url, opt.TimeoutSeconds, ct);
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var rate = HttpJson.Dec(root, "rate");
            var min = HttpJson.Dec(root, "min_amount");
            var max = HttpJson.Dec(root, "max_amount");
            if (rate is not > 0m) return null;
            if (min is > 0m && amount < min) return null;
            if (max is > 0m && amount > max) return null; // can't actually be filled at this size
            return rate;
        }
        catch { return null; }
    }

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}
