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
/// GET /estimate?coin1=xmr&coin2=usdt&amount=1&network1=xmr&network2=trc20&best=true
/// Response: { results: [{ exchange, amount, kycScore }], min, tradeValue_fiat, ... }
///   results[0].amount = units of coin2 received for `amount` of coin1
///   (best=true returns only the top provider)
///
/// SELL (XMR→USDT): coin1=xmr, coin2=usdt, amount=1
///   → results[0].amount = USDT per 1 XMR (direct sell price)
///
/// BUY  (USDT→XMR): coin1=usdt, coin2=xmr, amount=probe
///   → results[0].amount = XMR received → buyPrice = probe / amount
///
/// MinAmountUsd = min * (tradeValue_fiat / depositAmount)
///
/// If amount < min, retry at min * 1.1
/// </summary>
public sealed class CypherGoatClient : ICypherGoatClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

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

        var (amount, min, tvFiat) = await EstimateAsync(
            coin1: b.Coin, network1: b.Network,
            coin2: q.Coin, network2: q.Network,
            depositAmount: 1m, ct);

        if (amount is null && min is > 0m)
        {
            var probe = min.Value * 1.1m;
            (amount, _, tvFiat) = await EstimateAsync(
                b.Coin, b.Network,
                q.Coin, q.Network,
                probe, ct);
            if (amount is null or <= 0m) return null;
            return MakeResult(query, amount.Value / probe, CalcMinUsd(min, tvFiat, probe));
        }

        if (amount is null or <= 0m) return null;
        // amount = quote received per 1 base = sell price.
        return MakeResult(query, amount.Value, CalcMinUsd(min, tvFiat, 1m));
    }

    // ── BUY: Quote → Base (USDT/BTC/ETH → XMR) ───────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var b = Resolve(query.Base);
        var q = Resolve(query.Quote);

        // Probe is denominated in the QUOTE currency.
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;

        var (amount, min, tvFiat) = await EstimateAsync(
            coin1: q.Coin, network1: q.Network,
            coin2: b.Coin, network2: b.Network,
            depositAmount: probe, ct);

        if (amount is null && min is > 0m)
        {
            probe = min.Value * 1.1m;
            (amount, _, tvFiat) = await EstimateAsync(
                q.Coin, q.Network,
                b.Coin, b.Network,
                probe, ct);
        }

        if (amount is null or <= 0m) return null;
        // amount = base received for `probe` of quote → quote spent per 1 base.
        return MakeResult(query, probe / amount.Value, CalcMinUsd(min, tvFiat, probe));
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
    // Returns (bestAmount, minAmount, tradeValueFiat).
    // bestAmount     = best exchange's output amount for depositAmount of coin1.
    // minAmount      = minimum deposit; bestAmount is null when below it.
    // tradeValueFiat = USD value of depositAmount of coin1 (used to derive MinAmountUsd).

    private async Task<(decimal? bestAmount, decimal? minAmount, decimal? tradeValueFiat)> EstimateAsync(
        string coin1, string network1,
        string coin2, string network2,
        decimal depositAmount, CancellationToken ct)
    {
        var qs = $"coin1={Uri.EscapeDataString(coin1.ToLowerInvariant())}" +
                 $"&coin2={Uri.EscapeDataString(coin2.ToLowerInvariant())}" +
                 $"&amount={depositAmount.ToString(CultureInfo.InvariantCulture)}" +
                 $"&network1={Uri.EscapeDataString(network1.ToLowerInvariant())}" +
                 $"&network2={Uri.EscapeDataString(network2.ToLowerInvariant())}" +
                 $"&best=true";

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/estimate?{qs}";
        var body = await GetAsync(fullUrl, ct);
        if (body is null) return (null, null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Parse minimum deposit
            decimal? min = root.TryGetProperty("min", out var minEl) ? ReadDecimal(minEl) : null;
            if (min <= 0m) min = null;

            // Parse trade value in fiat (USD value of the requested depositAmount)
            decimal? tvFiat = root.TryGetProperty("tradeValue_fiat", out var tvEl) ? ReadDecimal(tvEl) : null;
            if (tvFiat <= 0m) tvFiat = null;

            if (min is > 0m && depositAmount < min.Value)
            {
                ExchangeLog.Debug($"[CYPHERGOAT] below min {min} for {coin1}→{coin2} amount={depositAmount}");
                return (null, min, tvFiat);
            }

            // Format 1: { "results": [{ "amount": 323.661, ... }] }  (docs format)
            if (root.TryGetProperty("results", out var results) &&
                results.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in results.EnumerateArray())
                {
                    if (item.TryGetProperty("amount", out var amtEl))
                    {
                        var v = ReadDecimal(amtEl);
                        if (v > 0m) return (v, min, tvFiat);
                    }
                }
            }

            // Format 2: { "rates": { "Amount": 323.661, "ExchangeName": "FixedFloat" } }
            if (root.TryGetProperty("rates", out var rates))
            {
                if (rates.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in new[] { "Amount", "amount" })
                    {
                        if (rates.TryGetProperty(prop, out var amtEl))
                        {
                            var v = ReadDecimal(amtEl);
                            if (v > 0m) return (v, min, tvFiat);
                        }
                    }
                }
                else if (rates.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in rates.EnumerateArray())
                    {
                        foreach (var prop in new[] { "Amount", "amount" })
                        {
                            if (item.TryGetProperty(prop, out var amtEl))
                            {
                                var v = ReadDecimal(amtEl);
                                if (v > 0m) return (v, min, tvFiat);
                            }
                        }
                    }
                }
            }

            ExchangeLog.Debug($"[CYPHERGOAT] no usable amount in: {body}");
            return (null, min, tvFiat);
        }
        catch (Exception ex)
        {
            ExchangeLog.Debug($"[CYPHERGOAT] parse error: {ex.Message} — {body}");
            return (null, null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> GetAsync(string fullUrl, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        ExchangeLog.Debug($"[CYPHERGOAT] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opt.ApiKey}");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                ExchangeLog.Debug($"[CYPHERGOAT] Timed out");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            ExchangeLog.Debug($"[CYPHERGOAT] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            ExchangeLog.Debug($"[CYPHERGOAT] Error: {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static decimal ReadDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private static decimal? CalcMinUsd(decimal? min, decimal? tradeValueFiat, decimal depositAmount) =>
        min is > 0m && tradeValueFiat is > 0m && depositAmount > 0m
            ? min.Value * (tradeValueFiat.Value / depositAmount)
            : null;

    private PriceResult MakeResult(PriceQuery q, decimal price, decimal? minAmountUsd = null) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null, minAmountUsd);
}