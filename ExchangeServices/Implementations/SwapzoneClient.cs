using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ExchangeServices.Implementations;

/// <summary>
/// Swapzone instant-exchange aggregator client.
///
/// Endpoints (base = https://api.swapzone.io/v1/exchange/):
///   GET currencies                                              → [{ ticker, network, name }]
///   GET get-rate?from=&to=&amount=&rateType=&chooseRate=best    → { amountTo, quotaId, minAmount, ... }
/// Auth: <c>x-api-key</c> header on every request (a User-Agent is also required by the WAF).
///
/// SELL (per-XMR price): from=xmr, to=quote, amount=1 → Price = amountTo (quote per 1 XMR).
/// BUY: from=quote, to=xmr, amount=probe → Price = probe / amountTo (quote needed per 1 XMR).
/// chooseRate=best asks Swapzone for the best offer across its 15+ partners.
/// </summary>
public sealed class SwapzoneClient : ISwapzoneClient
{
    private readonly HttpClient http;
    private readonly SwapzoneOptions opt;

    public string ExchangeKey => "swapzone";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;   // "V" — aggregator (varies by partner)
    public decimal MinAmountUsd => opt.MinAmountUsd;

    // Swapzone quotes both float and fixed; the site resolves one rate type per exchange at startup.
    public string RateType =>
        opt.RateType.Equals("fixed", StringComparison.OrdinalIgnoreCase)
            ? RateTypes.Fixed
            : RateTypes.Float;

    private const decimal SellProbeXmr = 1m;
    // Buy probe (quote currency) used when the caller doesn't size one — i.e. the USDT path.
    // Must be a realistic amount, NOT the pair minimum: at tiny amounts fixed network/withdrawal
    // fees dominate and the quoted rate is wildly off (e.g. ~$600/XMR at the ~$19 minimum).
    private const decimal DefaultBuyProbeUsdt = 300m;

    private readonly SemaphoreSlim currenciesLock = new(1, 1);
    private List<ExchangeCurrency>? cachedCurrencies;
    private DateTime currenciesAtUtc = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SwapzoneClient(HttpClient http, IOptions<SwapzoneOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ── IExchangePriceApi: SELL (quote received for 1 XMR) ────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        var from = Ticker(query.Base);
        var to = Ticker(query.Quote);
        if (from.Length == 0 || to.Length == 0) return null;

        var dto = await GetRateAsync(from, to, SellProbeXmr, query.Fixed, ct);
        if (dto is null || dto.AmountTo <= 0m || dto.AmountFrom <= 0m) return null;

        // Per-XMR quote = quote received / XMR sent. Use amountFrom from the response so a
        // min-amount retry (which changes the amount sent) still yields the correct unit rate.
        var unitRate = dto.AmountTo / dto.AmountFrom;
        if (unitRate <= 0m) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: unitRate,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: dto.QuotaId,
            Raw: null);
    }

    // ── IExchangeBuyPriceApi: BUY (quote needed to receive 1 XMR) ─────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        var fromQuote = Ticker(query.Quote); // paying the quote asset
        var toXmr = Ticker(query.Base);      // receiving XMR
        if (fromQuote.Length == 0 || toXmr.Length == 0) return null;

        // Probe is denominated in the quote currency. The site sizes BTC/ETH; for USDT it leaves
        // it null, so use a realistic default (small amounts give fee-distorted rates).
        var probe = query.ProbeAmount is decimal p && p > 0 ? p : DefaultBuyProbeUsdt;

        var dto = await GetRateAsync(fromQuote, toXmr, probe, query.Fixed, ct);
        if (dto is null || dto.AmountTo <= 0m || dto.AmountFrom <= 0m) return null;

        // Quote needed per 1 XMR = quote sent / XMR received. Use amountFrom from the response
        // (not the requested probe) so a min-amount retry doesn't skew the price.
        var quotePerXmr = dto.AmountFrom / dto.AmountTo;
        if (quotePerXmr <= 0m) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: quotePerXmr,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: dto.QuotaId,
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

        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return Array.Empty<ExchangeCurrency>();

        await currenciesLock.WaitAsync(ct);
        try
        {
            if (cachedCurrencies is not null &&
                (DateTime.UtcNow - currenciesAtUtc).TotalSeconds < Math.Max(60, opt.CurrenciesCacheSeconds))
            {
                return cachedCurrencies;
            }

            var (status, body) = await SendAsync(HttpMethod.Get, "currencies", ct);
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

    // ── get-rate ──────────────────────────────────────────────────────────────
    // Rate is computed as amountTo / amountFrom using the response's own amounts, so it is
    // correct regardless of the probe size. Callers must probe with a realistic amount — a
    // near-minimum probe returns a fee-distorted rate that is NOT the true market rate.
    private async Task<RateDto?> GetRateAsync(string from, string to, decimal amount, bool fixedRate, CancellationToken ct)
    {
        var rateType = fixedRate ? "fixed" : opt.RateType;

        var qs =
            $"get-rate?from={Uri.EscapeDataString(from)}" +
            $"&to={Uri.EscapeDataString(to)}" +
            $"&amount={Uri.EscapeDataString(amount.ToString(CultureInfo.InvariantCulture))}" +
            $"&rateType={Uri.EscapeDataString(rateType)}" +
            "&chooseRate=best";

        var (_, body) = await SendAsync(HttpMethod.Get, qs, ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var amountTo = GetDecimal(root, "amountTo");
            if (amountTo <= 0m) return null; // error / below-min → no quote

            var amountFrom = GetDecimal(root, "amountFrom");
            if (amountFrom <= 0m) amountFrom = amount;

            return new RateDto
            {
                AmountFrom = amountFrom,
                AmountTo = amountTo,
                MinAmount = GetDecimal(root, "minAmount"),
                QuotaId = root.TryGetProperty("quotaId", out var q) && q.ValueKind == JsonValueKind.String
                    ? q.GetString()
                    : null
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
            var ticker = el.TryGetProperty("ticker", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(ticker)) continue;

            var network = el.TryGetProperty("network", out var n) && n.ValueKind == JsonValueKind.String
                ? (n.GetString() ?? "") : "";

            list.Add(new ExchangeCurrency(
                ExchangeId: ticker.Trim().ToLowerInvariant(),
                Ticker: ticker.Trim().ToUpperInvariant(),
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
            req.Headers.TryAddWithoutValidation("x-api-key", opt.ApiKey);
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

    private static string Ticker(AssetRef asset) => (asset.Ticker ?? string.Empty).Trim().ToLowerInvariant();

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
        public string? QuotaId { get; set; }
    }
}
