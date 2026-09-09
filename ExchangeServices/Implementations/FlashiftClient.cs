using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ExchangeServices.Implementations;

/// <summary>
/// Flashift instant-exchange aggregator client.
///
/// Endpoint (base = https://interfacev2.flashift.app/api/dev/v2/):
///   GET getEstimatedAmount?symbol_from=&amp;network_from=&amp;symbol_to=&amp;network_to=&amp;amount=
///     → { message, data:[{ provider_name, exchange_type: "floating"|"fixed", amount, min_amount, max_amount, tags }] }
/// Auth: <c>Authorization: Bearer &lt;ApiKey&gt;</c>.
///
/// One call returns offers of BOTH rate types; we keep the BEST (max received/sent) of each.
/// Network codes: XMR→xmr, BTC→btc, ETH→eth, USDT→trx (Tron). Only these are quoted.
///
/// SELL (per-XMR price): from=xmr, to=quote → Price = quote received per 1 XMR (= amount/probe).
/// BUY: from=quote, to=xmr → Price = quote needed per 1 XMR (= probe/amount).
///
/// Rate limit is 10 req/min, so results are cached per direction for <see cref="FlashiftOptions.RateCacheSeconds"/>
/// (shared across the transient client instances via a static cache) and fetches are serialized.
/// </summary>
public sealed class FlashiftClient : IFlashiftClient
{
    private readonly HttpClient http;
    private readonly FlashiftOptions opt;

    public string ExchangeKey => "flashift";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;   // "V" — aggregator (varies by provider)
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Float;       // supports both; fixed is gated via FixedCapableKeys + query.Fixed

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Static, shared across the (transient) client instances so the rate-limit cache actually holds.
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim FetchGate = new(1, 1);

    private sealed record CacheEntry(DateTime AtUtc, decimal? FloatRate, decimal? FixedRate);

    public FlashiftClient(HttpClient http, IOptions<FlashiftOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ── IExchangePriceApi: SELL (quote received for 1 XMR) ─────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;
        if (!Map(query.Base, out var fromSym, out var fromNet)) return null; // xmr
        if (!Map(query.Quote, out var toSym, out var toNet)) return null;    // btc/eth/usdt

        var probe = Round(query.ProbeAmount is decimal pa && pa > 0 ? pa : opt.SellProbeXmr);
        var (flt, fix) = await BestRatesAsync(fromSym, fromNet, toSym, toNet, probe, ct);
        var rate = query.Fixed ? fix : flt; // received(quote)/sent(xmr) = quote per 1 XMR
        if (rate is not decimal r || r <= 0m) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, r, DateTimeOffset.UtcNow);
    }

    // ── IExchangeBuyPriceApi: BUY (quote needed to receive 1 XMR) ──────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;
        if (!Map(query.Base, out var xmrSym, out var xmrNet)) return null;   // xmr (to)
        if (!Map(query.Quote, out var fromSym, out var fromNet)) return null; // btc/eth/usdt (from)

        var probe = Round(query.ProbeAmount is decimal p && p > 0 ? p : opt.DefaultBuyProbeUsdt);
        var (flt, fix) = await BestRatesAsync(fromSym, fromNet, xmrSym, xmrNet, probe, ct);
        var rate = query.Fixed ? fix : flt; // received(xmr)/sent(quote) = XMR per 1 quote
        if (rate is not decimal r || r <= 0m) return null;

        var quotePerXmr = 1m / r; // invert → quote needed per 1 XMR (best buy = most XMR received = min price)
        if (quotePerXmr <= 0m) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, quotePerXmr, DateTimeOffset.UtcNow);
    }

    // ── Best floating/fixed rate (received/sent) for a direction, cached ───────
    private async Task<(decimal? Float, decimal? Fixed)> BestRatesAsync(
        string fromSym, string fromNet, string toSym, string toNet, decimal amount, CancellationToken ct)
    {
        var key = $"{fromSym}:{fromNet}>{toSym}:{toNet}";
        var ttl = Math.Max(15, opt.RateCacheSeconds);

        if (Cache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.AtUtc).TotalSeconds < ttl)
            return (hit.FloatRate, hit.FixedRate);

        await FetchGate.WaitAsync(ct);
        try
        {
            // Re-check under the gate — another caller may have just refreshed this key.
            if (Cache.TryGetValue(key, out hit) && (DateTime.UtcNow - hit.AtUtc).TotalSeconds < ttl)
                return (hit.FloatRate, hit.FixedRate);

            var (flt, fix) = await FetchBestRatesAsync(fromSym, fromNet, toSym, toNet, amount, ct);

            // Only cache a successful fetch; on failure keep any prior (stale) entry so a single
            // slow/limited call doesn't blank the row — but return the fresh (null) result so the
            // caller doesn't publish stale-as-fresh when there was never a good value.
            if (flt is not null || fix is not null)
                Cache[key] = new CacheEntry(DateTime.UtcNow, flt, fix);

            return (flt, fix);
        }
        finally
        {
            FetchGate.Release();
        }
    }

    private async Task<(decimal? Float, decimal? Fixed)> FetchBestRatesAsync(
        string fromSym, string fromNet, string toSym, string toNet, decimal amount, CancellationToken ct)
    {
        var amt = amount.ToString("0.########", CultureInfo.InvariantCulture);
        var url = $"getEstimatedAmount?symbol_from={fromSym}&network_from={fromNet}" +
                  $"&symbol_to={toSym}&network_to={toNet}&amount={amt}";

        var body = await SendAsync(url, ct);
        if (string.IsNullOrWhiteSpace(body)) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return (null, null);

            decimal? bestFloat = null, bestFixed = null;
            foreach (var o in data.EnumerateArray())
            {
                if (o.ValueKind != JsonValueKind.Object) continue;
                var got = GetDecimal(o, "amount");
                if (got <= 0m) continue;
                var rate = got / amount; // received per 1 sent
                if (rate <= 0m) continue;

                var type = o.TryGetProperty("exchange_type", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? (tEl.GetString() ?? "").Trim().ToLowerInvariant()
                    : "";

                if (type.StartsWith("fix", StringComparison.Ordinal))
                {
                    if (bestFixed is null || rate > bestFixed) bestFixed = rate;
                }
                else // "floating" (default)
                {
                    if (bestFloat is null || rate > bestFloat) bestFloat = rate;
                }
            }
            return (bestFloat, bestFixed);
        }
        catch
        {
            return (null, null);
        }
    }

    // ── HTTP (single Bearer-authed GET with light retry) ───────────────────────
    private async Task<string?> SendAsync(string relative, CancellationToken ct)
    {
        var attempts = Math.Max(1, Math.Clamp(opt.RetryCount, 0, 3) + 1);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(HttpMethod.Get, relative);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + opt.ApiKey);
            if (!string.IsNullOrWhiteSpace(opt.UserAgent) && !req.Headers.UserAgent.Any())
                req.Headers.UserAgent.ParseAdd(opt.UserAgent);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            HttpResponseMessage? resp = null;
            try
            {
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (ShouldRetry(resp.StatusCode) && attempt < attempts - 1)
                {
                    resp.Dispose();
                    await BackoffAsync(attempt, ct);
                    continue;
                }
                return IsSuccess(resp.StatusCode) ? body : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return null;
            }
            catch (HttpRequestException)
            {
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return null;
            }
            finally
            {
                resp?.Dispose();
            }
        }
        return null;
    }

    private static bool IsSuccess(HttpStatusCode code) => (int)code is >= 200 and < 300;

    private static bool ShouldRetry(HttpStatusCode code)
    {
        var n = (int)code;
        return code == HttpStatusCode.RequestTimeout || n == 429 || (n >= 500 && n <= 599);
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = Math.Min(2000, (int)(300 * Math.Pow(2, attempt))) + Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }

    // ── Ticker/network mapping to Flashift's codes ─────────────────────────────
    // XMR→xmr, BTC→btc, ETH→eth, USDT→trx (Tron). Anything else is unsupported → skipped.
    private static bool Map(AssetRef asset, out string symbol, out string network)
    {
        symbol = network = string.Empty;
        var t = (asset.Ticker ?? string.Empty).Trim().ToLowerInvariant();
        switch (t)
        {
            case "xmr": symbol = "xmr"; network = "xmr"; return true;
            case "btc": symbol = "btc"; network = "btc"; return true;
            case "eth": symbol = "eth"; network = "eth"; return true;
            case "usdt": symbol = "usdt"; network = "trx"; return true; // Tron
            default: return false;
        }
    }

    // The probe MUST be ≤8 dp — a raw TargetTradeUsd/price decimal has ~27 digits and some
    // upstream validators reject it (see PriceService.RoundProbe); mirror that defensively.
    private static decimal Round(decimal v) => Math.Round(v, 8, MidpointRounding.ToZero);

    private static decimal GetDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var p)) return 0m;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m
        };
    }
}
