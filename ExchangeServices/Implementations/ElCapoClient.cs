using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ExchangeServices.Implementations;

/// <summary>
/// El Capo (https://elcapo.io) — no-KYC instant exchange.
///
/// Endpoints (base = https://elcapo.io/):
///   GET api/partner/rate?coin_from=&network_from=&coin_to=&network_to=&amount=&rate_type=
///        → { amount_from, amount_to, rate, min_amount, max_amount }   (X-API-Key required)
///   GET api/v1/currencies  → [{ id, network, name, min_amount }]       (public, no key)
/// Auth: <c>X-API-Key</c> header on partner requests.
///
/// SELL (per-XMR price): coin_from=XMR, coin_to=quote, amount=1 → Price = amount_to / amount_from.
/// BUY: coin_from=quote, coin_to=XMR, amount=probe → Price = amount_from / amount_to (quote per 1 XMR).
/// Supports rate_type "fixed" and "float"; the site resolves one rate type per exchange at startup.
/// </summary>
public sealed class ElCapoClient : IElCapoClient
{
    private readonly HttpClient http;
    private readonly ElCapoOptions opt;

    public string ExchangeKey => "elcapo";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;   // "A" — no-KYC verified
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public string RateType =>
        opt.RateType.Equals("fixed", StringComparison.OrdinalIgnoreCase)
            ? RateTypes.Fixed
            : RateTypes.Float;

    private const decimal SellProbeXmr = 1m;
    // Buy probe (quote currency) used when the caller doesn't size one (the USDT path). Must be a
    // realistic amount, NOT the pair minimum: at tiny amounts fixed network/withdrawal fees
    // dominate and the quoted rate is wildly off.
    private const decimal DefaultBuyProbeUsdt = 300m;

    private readonly SemaphoreSlim currenciesLock = new(1, 1);
    private List<ExchangeCurrency>? cachedCurrencies;
    private DateTime currenciesAtUtc = DateTime.MinValue;

    public ElCapoClient(HttpClient http, IOptions<ElCapoOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ── IExchangePriceApi: SELL (quote received for 1 XMR) ────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        var probe = query.ProbeAmount is decimal pa && pa > 0 ? pa : SellProbeXmr;
        var dto = await GetRateAsync(query.Base, query.Quote, probe, query.Fixed, ct);
        if (dto is null || dto.AmountTo <= 0m || dto.AmountFrom <= 0m) return null;

        // Per-XMR quote = quote received / XMR sent. Use amountFrom from the response so a
        // min-amount adjustment still yields the correct unit rate.
        var unitRate = dto.AmountTo / dto.AmountFrom;
        if (unitRate <= 0m) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: unitRate,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null);
    }

    // ── IExchangeBuyPriceApi: BUY (quote needed to receive 1 XMR) ─────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        // Probe is denominated in the quote currency. The site sizes BTC/ETH; for USDT it leaves
        // it null, so use a realistic default (small amounts give fee-distorted rates).
        var probe = query.ProbeAmount is decimal p && p > 0 ? p : DefaultBuyProbeUsdt;

        var dto = await GetRateAsync(query.Quote, query.Base, probe, query.Fixed, ct);
        if (dto is null || dto.AmountTo <= 0m || dto.AmountFrom <= 0m) return null;

        // Quote needed per 1 XMR = quote sent / XMR received.
        var quotePerXmr = dto.AmountFrom / dto.AmountTo;
        if (quotePerXmr <= 0m) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: quotePerXmr,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: null);
    }

    // ── IExchangeCurrencyApi ──────────────────────────────────────────────────
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (cachedCurrencies is not null &&
            (DateTime.UtcNow - currenciesAtUtc).TotalSeconds < Math.Max(60, opt.CurrenciesCacheSeconds))
        {
            return cachedCurrencies;
        }

        await currenciesLock.WaitAsync(ct);
        try
        {
            if (cachedCurrencies is not null &&
                (DateTime.UtcNow - currenciesAtUtc).TotalSeconds < Math.Max(60, opt.CurrenciesCacheSeconds))
            {
                return cachedCurrencies;
            }

            // Public endpoint — no key required.
            var (status, body) = await SendAsync(HttpMethod.Get, "api/v1/currencies", ct);
            if (status is null || !IsSuccess(status.Value) || string.IsNullOrWhiteSpace(body))
                return cachedCurrencies ?? (IReadOnlyList<ExchangeCurrency>)Array.Empty<ExchangeCurrency>();

            var parsed = ParseCurrencies(body);
            if (parsed.Count > 0)
            {
                cachedCurrencies = parsed;
                currenciesAtUtc = DateTime.UtcNow;
            }
            return cachedCurrencies ?? (IReadOnlyList<ExchangeCurrency>)Array.Empty<ExchangeCurrency>();
        }
        finally
        {
            currenciesLock.Release();
        }
    }

    // ── /rate ─────────────────────────────────────────────────────────────────
    // Rate is computed as amount_to / amount_from using the response's own amounts, so it is
    // correct regardless of the probe size. Callers must probe with a realistic amount — a
    // near-minimum probe returns a fee-distorted rate that is NOT the true market rate.
    private async Task<RateDto?> GetRateAsync(AssetRef from, AssetRef to, decimal amount, bool fixedRate, CancellationToken ct)
    {
        var coinFrom = Coin(from);
        var coinTo = Coin(to);
        if (coinFrom.Length == 0 || coinTo.Length == 0) return null;

        var rateType = fixedRate ? "fixed" : (opt.RateType.Equals("fixed", StringComparison.OrdinalIgnoreCase) ? "fixed" : "float");

        var qs =
            $"api/partner/rate?coin_from={Uri.EscapeDataString(coinFrom)}" +
            $"&network_from={Uri.EscapeDataString(Net(from))}" +
            $"&coin_to={Uri.EscapeDataString(coinTo)}" +
            $"&network_to={Uri.EscapeDataString(Net(to))}" +
            $"&amount={Uri.EscapeDataString(amount.ToString(CultureInfo.InvariantCulture))}" +
            $"&rate_type={Uri.EscapeDataString(rateType)}";

        var (_, body) = await SendAsync(HttpMethod.Get, qs, ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var amountTo = GetDecimal(root, "amount_to");
            if (amountTo <= 0m) return null; // error / below-min → no quote

            var amountFrom = GetDecimal(root, "amount_from");
            if (amountFrom <= 0m) amountFrom = amount;

            return new RateDto
            {
                AmountFrom = amountFrom,
                AmountTo = amountTo,
                MinAmount = GetDecimal(root, "min_amount"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<ExchangeCurrency> ParseCurrencies(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<ExchangeCurrency>();

        var list = new List<ExchangeCurrency>(doc.RootElement.GetArrayLength());
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;

            // Skip currencies the exchange has marked inactive.
            if (el.TryGetProperty("is_active", out var act) && act.ValueKind == JsonValueKind.False)
                continue;

            var id = el.TryGetProperty("id", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var network = el.TryGetProperty("network", out var n) && n.ValueKind == JsonValueKind.String
                ? (n.GetString() ?? "") : "";

            list.Add(new ExchangeCurrency(
                ExchangeId: id.Trim().ToLowerInvariant(),
                Ticker: id.Trim().ToUpperInvariant(),
                Network: network));
        }
        return list;
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private async Task<(HttpStatusCode? Status, string? Body)> SendAsync(HttpMethod method, string relative, CancellationToken ct)
    {
        var attempts = Math.Max(1, Math.Clamp(opt.RetryCount, 0, 5) + 1);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(method, relative);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(opt.ApiKey))
                req.Headers.TryAddWithoutValidation("X-API-Key", opt.ApiKey);
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
                return (resp.StatusCode, body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return (null, null);
            }
            catch (HttpRequestException)
            {
                if (attempt < attempts - 1) { await BackoffAsync(attempt, ct); continue; }
                return (null, null);
            }
            finally
            {
                resp?.Dispose();
            }
        }
        return (null, null);
    }

    private static bool IsSuccess(HttpStatusCode code) => (int)code is >= 200 and < 300;

    private static bool ShouldRetry(HttpStatusCode code)
    {
        var n = (int)code;
        return code == HttpStatusCode.RequestTimeout || n == 429 || (n >= 500 && n <= 599);
    }

    private static Task BackoffAsync(int attempt, CancellationToken ct)
    {
        var ms = Math.Min(2000, (int)(200 * Math.Pow(2, attempt))) + Random.Shared.Next(0, 200);
        return Task.Delay(ms, ct);
    }

    // El Capo requires both a coin and its network; for single-network coins (XMR/BTC/ETH) the
    // network equals the ticker. Tickers/networks are sent uppercase to match the API examples.
    private static string Coin(AssetRef a) => (a.Ticker ?? string.Empty).Trim().ToUpperInvariant();

    // Map the site's network names to El Capo's (it uses TRX for Tron and ETH for Ethereum).
    private static string Net(AssetRef a)
    {
        var net = (string.IsNullOrWhiteSpace(a.Network) ? (a.Ticker ?? string.Empty) : a.Network!).Trim().ToUpperInvariant();
        return net switch
        {
            "TRON" or "TRC20" or "TRC-20" => "TRX",
            "ETHEREUM" or "ERC20" or "ERC-20" => "ETH",
            _ => net,
        };
    }

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

    private sealed class RateDto
    {
        public decimal AmountFrom { get; set; }
        public decimal AmountTo { get; set; }
        public decimal MinAmount { get; set; }
    }
}
