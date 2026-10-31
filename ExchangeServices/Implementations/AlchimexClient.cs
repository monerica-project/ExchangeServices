using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class AlchimexOptions
{
    public string BaseUrl { get; set; } = "https://alchimex.io/api/v1/";
    public string SiteName { get; set; } = "Alchimex";
    public string? SiteUrl { get; set; } = "https://alchimex.io";

    /// <summary>Optional partner key (raises the live-quote limit from 10 to 60/min). Keep it in
    /// appsettings.Production.json only, never in a tracked file.</summary>
    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; } = 'B';
    public decimal MinAmountUsd { get; set; } = 40m;

    /// <summary>How long a quote is reused per direction. The live-quote limit is 10/min per key,
    /// shared by every app on the same server, so keep this generous.</summary>
    public int RateCacheSeconds { get; set; } = 120;

    /// <summary>While rate-limited (HTTP 429), keep serving the last good quote up to this age.</summary>
    public int StaleRateSeconds { get; set; } = 600;
}

/// <summary>
/// Alchimex (https://alchimex.io), a no-account swap that fixes the rate when a batch is created.
/// GET rate?from&amp;to&amp;amount → { amount_from, amount_to } with the 0.5% fee already included.
/// Assets are codes with the network built in: XMR, BTC, ETH, USDT (Ethereum), USDT-TRX (Tron).
/// USDT-TRX → XMR is sometimes refused ("invalid_request"), so buys fall back to USDT on Ethereum.
/// Live quotes are limited to 10/min (even with a key), so quotes are cached per direction, refused
/// directions are remembered, and a 429 pauses all calls until Retry-After while the last good quote
/// keeps being served.
/// </summary>
public sealed class AlchimexClient : IAlchimexClient
{
    // Per direction: last good rate (Rate > 0) or a remembered refusal (Rate = 0).
    private static readonly ConcurrentDictionary<string, (DateTime At, decimal Rate)> Cache = new();
    private static long blockedUntilTicks; // UTC ticks; no calls before this after a 429

    private readonly HttpClient http;
    private readonly AlchimexOptions opt;

    public AlchimexClient(HttpClient http, IOptions<AlchimexOptions> options)
    {
        this.http = http;
        opt = options.Value;
    }

    public string ExchangeKey => "alchimex";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Fixed; // the rate is always fixed at batch creation (fixed board only on MoneroPriceNow)

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = query.ProbeAmount is decimal p && p > 0 ? ProbeSizing.Round(p) : 1m;
        var rate = await RateAsync(Code(query.Base), Code(query.Quote), probe, ct);
        return rate is > 0m ? Result(query, rate.Value) : null;
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = ProbeSizing.BuyProbe(query, 2500m);
        var from = Code(query.Quote);
        var rate = await RateAsync(from, Code(query.Base), probe, ct); // XMR per quote unit
        if (rate is not > 0m && from == "USDT-TRX")
        {
            rate = await RateAsync("USDT", Code(query.Base), probe, ct);
        }

        return rate is > 0m ? Result(query, 1m / rate.Value) : null;
    }

    internal static string Code(AssetRef a)
    {
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        if (t is "USDT" or "USDC")
        {
            var n = (a.Network ?? "").Trim().ToLowerInvariant();
            return n is "ethereum" or "erc20" or "eth" ? t : $"{t}-TRX";
        }

        return t;
    }

    private async Task<decimal?> RateAsync(string from, string to, decimal amount, CancellationToken ct)
    {
        if (from.Length == 0 || to.Length == 0 || from == to) return null;

        var now = DateTime.UtcNow;
        var cacheKey = $"{from}>{to}";
        var hasHit = Cache.TryGetValue(cacheKey, out var hit);
        if (hasHit && now - hit.At < TimeSpan.FromSeconds(opt.RateCacheSeconds))
        {
            return hit.Rate > 0m ? hit.Rate : null;
        }

        decimal? Stale() => hasHit && hit.Rate > 0m && now - hit.At < TimeSpan.FromSeconds(opt.StaleRateSeconds)
            ? hit.Rate
            : null;

        if (now.Ticks < Interlocked.Read(ref blockedUntilTicks))
        {
            return Stale();
        }

        var url = $"rate?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}" +
                  $"&amount={amount.ToString("0.########", CultureInfo.InvariantCulture)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + opt.ApiKey);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 2, 60)));
        try
        {
            using var resp = await http.SendAsync(req, cts.Token);
            if ((int)resp.StatusCode == 429)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                Interlocked.Exchange(ref blockedUntilTicks, DateTime.UtcNow.Add(wait).Ticks);
                return Stale();
            }

            if ((int)resp.StatusCode == 422)
            {
                // Refused direction (no route / invalid for this pair): remember it, don't retry every cycle.
                Cache[cacheKey] = (DateTime.UtcNow, 0m);
                return null;
            }

            if (!resp.IsSuccessStatusCode) return Stale();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out _)) return Stale();

            // Rate from the response's own amounts (not the requested amount).
            var sent = HttpJson.Dec(root, "amount_from");
            var got = HttpJson.Dec(root, "amount_to");
            if (sent is not > 0m || got is not > 0m) return Stale();

            var rate = got.Value / sent.Value;
            Cache[cacheKey] = (DateTime.UtcNow, rate);
            return rate;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Stale();
        }
    }

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}
