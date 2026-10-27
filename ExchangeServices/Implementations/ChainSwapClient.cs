using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class ChainSwapOptions
{
    public string BaseUrl { get; set; } = "https://chainswap.io/";
    public string SiteName { get; set; } = "ChainSwap.io";
    public string? SiteUrl { get; set; } = "https://chainswap.io";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "Mozilla/5.0 (compatible; CryptoPriceNow/1.0)";
    public char PrivacyLevel { get; set; } = 'B';
    public decimal MinAmountUsd { get; set; } = 0m;

    /// <summary>How long a homepage session (cookie + CSRF token) is reused before fetching a new one.</summary>
    public int SessionMinutes { get; set; } = 20;
}

/// <summary>
/// ChainSwap.io (https://chainswap.io), keyless. No public API: uses the same call as the site's own
/// swap form. GET / gives a session cookie + csrf-token meta tag; then POST get-exchange-amount (form)
/// { from_amount, currency_from, currency_to, from_network, to_network, rate_mode=dynamic } →
/// { status:"success", to_amount, min_amount }. USDT is quoted on ERC20 (Tron is often unavailable).
/// </summary>
public sealed partial class ChainSwapClient : IChainSwapClient
{
    private readonly HttpClient http;
    private readonly ChainSwapOptions opt;

    // One session shared by every instance: the client is transient but the site session is not.
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static (string Cookie, string Token, DateTimeOffset Expires)? session;

    public ChainSwapClient(HttpClient http, IOptions<ChainSwapOptions> options)
    {
        this.http = http;
        opt = options.Value;
    }

    public string ExchangeKey => "chainswap";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Float;

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = query.ProbeAmount is decimal p && p > 0 ? ProbeSizing.Round(p) : 1m;
        var received = await AmountAsync(query.Base, query.Quote, probe, ct);
        return received is > 0m ? Result(query, received.Value / probe) : null;
    }

    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = ProbeSizing.BuyProbe(query, 2500m);
        var received = await AmountAsync(query.Quote, query.Base, probe, ct);
        return received is > 0m ? Result(query, probe / received.Value) : null;
    }

    internal static (string Coin, string Network)? Code(AssetRef a)
    {
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        return t switch
        {
            "XMR" or "BTC" or "ETH" or "LTC" => (t, t),
            "USDT" => ("USDT", "ERC20"),
            _ => null,
        };
    }

    // ChainSwap is slow (often 4-6s per quote, plus a homepage fetch for a new session), which can
    // overrun the board's per-exchange budget. So keep the last rate per direction and answer from it
    // while a background refresh runs (stale-while-revalidate). Rates older than RateMaxAgeSeconds are dropped.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (decimal Rate, DateTimeOffset At)> Rates = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Refreshing = new();
    private const int RateFreshSeconds = 25;
    private const int RateMaxAgeSeconds = 120;

    private async Task<decimal?> AmountAsync(AssetRef from, AssetRef to, decimal amount, CancellationToken ct)
    {
        var f = Code(from);
        var t = Code(to);
        if (f is null || t is null || f.Value.Coin == t.Value.Coin) return null;

        var key = $"{f.Value.Coin}:{f.Value.Network}>{t.Value.Coin}:{t.Value.Network}";
        var now = DateTimeOffset.UtcNow;
        if (Rates.TryGetValue(key, out var cached) && now - cached.At < TimeSpan.FromSeconds(RateMaxAgeSeconds))
        {
            if (now - cached.At > TimeSpan.FromSeconds(RateFreshSeconds) && Refreshing.TryAdd(key, 0))
            {
                _ = Task.Run(async () =>
                {
                    try { await FetchRateAsync(key, f.Value, t.Value, amount, CancellationToken.None); }
                    finally { Refreshing.TryRemove(key, out _); }
                });
            }
            return cached.Rate * amount;
        }

        var rate = await FetchRateAsync(key, f.Value, t.Value, amount, ct);
        return rate is > 0m ? rate * amount : null;
    }

    private async Task<decimal?> FetchRateAsync(string key, (string Coin, string Network) f, (string Coin, string Network) t, decimal amount, CancellationToken ct)
    {
        var received = await QuoteAsync(f, t, amount, ct);
        if (received is not > 0m) { Rates.TryRemove(key, out _); return null; }
        var rate = received.Value / amount;
        Rates[key] = (rate, DateTimeOffset.UtcNow);
        return rate;
    }

    private async Task<decimal?> QuoteAsync((string Coin, string Network) f, (string Coin, string Network) t, decimal amount, CancellationToken ct)
    {

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var s = await GetSessionAsync(forceNew: attempt > 0, ct);
            if (s is null) return null;

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["from_amount"] = amount.ToString("0.########", CultureInfo.InvariantCulture),
                ["currency_from"] = f.Coin,
                ["currency_to"] = t.Coin,
                ["from_network"] = f.Network,
                ["to_network"] = t.Network,
                ["rate_mode"] = "dynamic",
            });
            var req = new HttpRequestMessage(HttpMethod.Post, "get-exchange-amount") { Content = form };
            req.Headers.TryAddWithoutValidation("Cookie", s.Value.Cookie);
            req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", s.Value.Token);
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");

            var body = await HttpJson.SendAsync(http, req, opt.TimeoutSeconds, ct);
            if (body is null) continue; // expired session (419) or network error: retry once with a new session
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("status", out var st) || st.GetString() != "success") return null;
                var min = HttpJson.Dec(root, "min_amount");
                if (min is > 0m && amount < min) return null;
                return HttpJson.Dec(root, "to_amount");
            }
            catch { return null; }
        }
        return null;
    }

    private async Task<(string Cookie, string Token)?> GetSessionAsync(bool forceNew, CancellationToken ct)
    {
        var cur = session;
        if (!forceNew && cur is { } c && c.Expires > DateTimeOffset.UtcNow) return (c.Cookie, c.Token);

        await SessionGate.WaitAsync(ct);
        try
        {
            cur = session;
            if (!forceNew && cur is { } c2 && c2.Expires > DateTimeOffset.UtcNow) return (c2.Cookie, c2.Token);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 2, 60)));
            using var resp = await http.GetAsync("", cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(cts.Token);

            var m = CsrfMeta().Match(html);
            if (!m.Success) return null;

            var cookie = string.Join("; ",
                (resp.Headers.TryGetValues("Set-Cookie", out var sc) ? sc : Array.Empty<string>())
                .Select(v => v.Split(';', 2)[0].Trim())
                .Where(v => v.Contains('=')));
            if (cookie.Length == 0) return null;

            session = (cookie, m.Groups[1].Value, DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(opt.SessionMinutes, 1, 100)));
            return (cookie, m.Groups[1].Value);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        finally { SessionGate.Release(); }
    }

    [GeneratedRegex("<meta\\s+name=\"csrf-token\"\\s+content=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex CsrfMeta();

    private PriceResult Result(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}
