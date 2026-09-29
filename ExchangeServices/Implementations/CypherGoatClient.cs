using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// CypherGoat API client — crypto exchange aggregator.
/// Docs: https://api.cyphergoat.com
///
/// Auth: Authorization: Bearer YOUR_API_KEY
///
/// IMPORTANT: this client always calls /estimate with best=false, never
/// best=true. best=true's success body is only { rates: { ExchangeName, Amount } }
/// — no min, no tradeValue_fiat — and its failure body is a bare
/// { error: "error getting rate" } with no numbers in it at all. Neither
/// piece of metadata this client needs is recoverable from that shape.
///
/// best=false gives us what we actually need:
///   Success: { rates: { Results: [{Exchange,Amount,KYCScore,Markup}], TradeValue_fiat,
///                        TradeValue_btc, EstimateId }, min: <static per-coin1 minimum> }
///     (rates.Min is always 0 server-side — dead field, ignore it; the real
///     minimum on a successful call is the top-level "min".)
///   Failure (amount below minimum): 404 with
///     { error: "amount is less than the minimum value of 0.010000 for xmr" }
///     — the minimum is only ever surfaced as text inside this message, so we
///     regex it out. (Other failures, e.g. no route for the pair at all, 404
///     with an error that doesn't mention "amount" — no minimum to recover.)
///
/// SELL (XMR→USDT): coin1=xmr, coin2=usdt, amount=1
///   → best result's Amount = USDT per 1 XMR (direct sell price)
///
/// BUY  (USDT→XMR): coin1=usdt, coin2=xmr, amount=probe
///   → best result's Amount = XMR received → buyPrice = probe / amount
///
/// MinAmountUsd = min * (tradeValue_fiat / depositAmount), both taken from the
/// SAME successful call (either the first probe, or the min*1.1 retry).
///
/// If the probe amount < min, retry once at min * 1.1 (min learned from the
/// failed call's error text).
/// </summary>
public sealed class CypherGoatClient : ICypherGoatClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // "amount is less than the minimum value of 0.010000 for xmr"
    private static readonly Regex MinFromErrorRegex = new(
        @"minimum value of\s*(?<min>[0-9]*\.?[0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly CypherGoatOptions opt;

    public string ExchangeKey => "cyphergoat";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public CypherGoatClient(HttpClient http, IOptions<CypherGoatOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── Asset resolution ───────────────────────────────────────────────────────
    // Map an AssetRef (ticker + optional network) → CypherGoat (coin, network).
    // Both lowercased per the API. A bare ticker resolves to its native chain
    // (btc→btc, eth→eth, xmr→xmr); usdt falls back to opt.UsdtNetwork so the
    // XMR/USDT pair behaves exactly as before.
    private (string Coin, string Network) Resolve(AssetRef a)
    {
        var coin = (a.Ticker ?? "").Trim().ToLowerInvariant();
        var net = (a.Network ?? "").Trim().ToLowerInvariant();
        if (net.Length == 0)
            net = coin switch
            {
                "usdt" => opt.UsdtNetwork.Trim().ToLowerInvariant(),
                _ => coin,   // native chain
            };
        return (coin, net);
    }

    // ── SELL: Base → Quote (XMR → USDT/BTC/ETH) ──────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var b = Resolve(query.Base);
        var q = Resolve(query.Quote);

        var probe = query.ProbeAmount is decimal pa && pa > 0 ? pa : 1m;
        var result = await EstimateAsync(b.Coin, b.Network, q.Coin, q.Network, probe, ct);

        if (result.Amount is null && result.Min is > 0m)
        {
            probe = result.Min.Value * 1.1m;
            result = await EstimateAsync(b.Coin, b.Network, q.Coin, q.Network, probe, ct);
        }

        if (result.Amount is null or <= 0m) return null;
        // Amount = quote received for `probe` base; per-unit sell price = received / sent.
        return MakeResult(query, result.Amount.Value / probe, CalcMinUsd(result.Min, result.TradeValueFiat, probe));
    }

    // ── BUY: Quote → Base (USDT/BTC/ETH → XMR) ───────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var b = Resolve(query.Base);
        var q = Resolve(query.Quote);

        // Probe is denominated in the QUOTE currency.
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;
        var result = await EstimateAsync(q.Coin, q.Network, b.Coin, b.Network, probe, ct);

        if (result.Amount is null && result.Min is > 0m)
        {
            probe = result.Min.Value * 1.1m;
            result = await EstimateAsync(q.Coin, q.Network, b.Coin, b.Network, probe, ct);
        }

        if (result.Amount is null or <= 0m) return null;
        // Amount = base received for `probe` of quote → quote spent per 1 base.
        return MakeResult(query, probe / result.Amount.Value, CalcMinUsd(result.Min, result.TradeValueFiat, probe));
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    // api.cyphergoat.com has no coins/currencies endpoint (only /estimate, /swap,
    // /transaction). CypherGoat's full supported-coin list is rendered server-side
    // into the public homepage (opt.CoinListUrl) inside the coin-selection modal as
    //   <div data-ticker="usdt" data-network="tron" data-name="Tether USD" ...>
    // where data-ticker/data-network are exactly the coin1/network1 the /estimate
    // call consumes (both lowercased). We fetch that page and parse those tuples.
    private static readonly Regex CoinDivRegex = new(
        "<div\\s+data-ticker=\"(?<ticker>[^\"]*)\"\\s+data-network=\"(?<network>[^\"]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var html = await GetAsync(opt.CoinListUrl, ct);
        if (string.IsNullOrEmpty(html)) return Array.Empty<ExchangeCurrency>();

        return CoinDivRegex.Matches(html)
            .Select(m => (
                Ticker: m.Groups["ticker"].Value.Trim(),
                Network: m.Groups["network"].Value.Trim()))
            .Where(x => x.Ticker.Length > 0 && x.Network.Length > 0)
            .Select(x => new ExchangeCurrency(
                ExchangeId: $"{x.Ticker}|{x.Network}".ToLowerInvariant(),
                Ticker: x.Ticker.ToUpperInvariant(),
                Network: x.Network.ToLowerInvariant()))
            .GroupBy(c => c.ExchangeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Ticker, StringComparer.Ordinal)
            .ThenBy(c => c.Network, StringComparer.Ordinal)
            .ToList();
    }

    // ── Core estimate call ────────────────────────────────────────────────────

    private readonly record struct EstimateResult(
        decimal? Amount,
        string? Exchange,
        decimal? Min,
        decimal? TradeValueFiat);

    // Response shapes for best=false. Field names match the Go struct's
    // (un-tagged, so json.Marshal emits its Go-cased field names verbatim);
    // PropertyNameCaseInsensitive handles the case match.
    private sealed class DetailedEstimateResponse
    {
        public RatesObject? Rates { get; set; }
        public decimal Min { get; set; } // static per-coin1 minimum (only present on success)
    }

    private sealed class RatesObject
    {
        public List<ResultItem>? Results { get; set; }
        public decimal TradeValue_fiat { get; set; }
    }

    private sealed class ResultItem
    {
        public string? Exchange { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class ErrorResponse
    {
        public string? Error { get; set; }
    }

    private async Task<EstimateResult> EstimateAsync(
        string coin1, string network1,
        string coin2, string network2,
        decimal depositAmount, CancellationToken ct)
    {
        var qs = $"coin1={Uri.EscapeDataString(coin1)}" +
                 $"&coin2={Uri.EscapeDataString(coin2)}" +
                 $"&amount={depositAmount.ToString(CultureInfo.InvariantCulture)}" +
                 $"&network1={Uri.EscapeDataString(network1)}" +
                 $"&network2={Uri.EscapeDataString(network2)}" +
                 $"&best=false";

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/estimate?{qs}";
        var (body, statusCode) = await GetWithStatusAsync(fullUrl, ct);
        if (body is null) return default;

        try
        {
            if (statusCode is >= 200 and < 300)
            {
                var parsed = JsonSerializer.Deserialize<DetailedEstimateResponse>(body, JsonOpt);
                var best = parsed?.Rates?.Results?
                    .Where(r => r.Amount > 0m)
                    .OrderByDescending(r => r.Amount)
                    .FirstOrDefault();

                if (best is null)
                {
                    ExchangeLog.Debug($"[CYPHERGOAT] no usable amount in: {body}");
                    return new EstimateResult(null, null, parsed?.Min, parsed?.Rates?.TradeValue_fiat);
                }

                return new EstimateResult(best.Amount, best.Exchange, parsed!.Min, parsed.Rates!.TradeValue_fiat);
            }

            // Non-2xx: the only recoverable info is the minimum, and only when
            // the error text says so — see MinFromErrorRegex doc comment above.
            var err = JsonSerializer.Deserialize<ErrorResponse>(body, JsonOpt)?.Error ?? "";
            var m = MinFromErrorRegex.Match(err);
            decimal? min = m.Success && decimal.TryParse(
                m.Groups["min"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mv) && mv > 0m
                ? mv
                : null;

            ExchangeLog.Debug($"[CYPHERGOAT] HTTP {statusCode} for {coin1}→{coin2} amount={depositAmount}: {err}");
            return new EstimateResult(null, null, min, null);
        }
        catch (Exception ex)
        {
            ExchangeLog.Debug($"[CYPHERGOAT] parse error: {ex.Message} — {body}");
            return default;
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> GetAsync(string fullUrl, CancellationToken ct)
    {
        var (body, status) = await GetWithStatusAsync(fullUrl, ct);
        return status is >= 200 and < 300 ? body : null;
    }

    private async Task<(string? Body, int StatusCode)> GetWithStatusAsync(string fullUrl, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        ExchangeLog.Debug($"[CYPHERGOAT] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opt.ApiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var resp = await _http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(ct);

            ExchangeLog.Debug($"[CYPHERGOAT] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            // We deliberately return the body even for non-2xx responses —
            // 404s here carry the only source of minimum-amount info.
            return (body, (int)resp.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ExchangeLog.Debug($"[CYPHERGOAT] Timed out");
            return (null, 0);
        }
        catch (Exception ex)
        {
            ExchangeLog.Debug($"[CYPHERGOAT] Error: {ex.Message}");
            return (null, 0);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static decimal? CalcMinUsd(decimal? min, decimal? tradeValueFiat, decimal depositAmount) =>
        min is > 0m && tradeValueFiat is > 0m && depositAmount > 0m
            ? min.Value * (tradeValueFiat.Value / depositAmount)
            : null;

    private PriceResult MakeResult(PriceQuery q, decimal price, decimal? minAmountUsd = null) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null, minAmountUsd);
}
