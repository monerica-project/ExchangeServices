using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

/// <summary>
/// n.exchange (Nexchange) API v2 client. https://api.n.exchange/docs/v2/
///
/// Endpoints used:
///   GET /currency/                                   → full currency list (PUBLIC)
///   GET /rate/?from=&to=&deposit_amount=&crypto_only → exchange rate (requires API key)
///
/// Auth: the rate endpoint needs an "Authorization: ApiKey {key}" header. The
/// currency list is public, so coin discovery works with no key. An optional
/// "x-referral-token" header attributes rate quotes to a referral code.
///
/// Pricing: /rate/ returns the post-fee `withdraw_amount` (how much `to` you get
/// for `deposit_amount` of `from`), so the effective price is withdraw/deposit.
/// </summary>
public sealed class NexchangeClient : INexchangeClient
{
    private readonly HttpClient _http;
    private readonly NexchangeOptions opt;

    public string ExchangeKey => "nexchange";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    private const decimal SellProbeXmr = 1m;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public NexchangeClient(HttpClient http, IOptions<NexchangeOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── IExchangeCurrencyApi ──────────────────────────────────────────────────
    // GET /currency/ is public. Drop fiat; map each crypto to its common ticker
    // (e.g. USDTERC -> USDT) so it dedupes with the same coin from other exchanges.
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var body = await SendAsync(HttpMethod.Get, "currency/", ct);
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<ExchangeCurrency>();

        List<CurrencyDto>? list;
        try { list = JsonSerializer.Deserialize<List<CurrencyDto>>(body, JsonOpts); }
        catch { return Array.Empty<ExchangeCurrency>(); }
        if (list is null) return Array.Empty<ExchangeCurrency>();

        return list
            .Where(c => !c.IsFiat && !string.IsNullOrWhiteSpace(c.Code))
            .Select(c =>
            {
                var ticker = (string.IsNullOrWhiteSpace(c.CommonSymbol) ? c.Code : c.CommonSymbol!)
                    .Trim().ToUpperInvariant();
                var network = (c.Network ?? "").Trim();
                return new ExchangeCurrency(
                    ExchangeId: $"{c.Code}|{network}".ToLowerInvariant(),
                    Ticker: ticker,
                    Network: network);
            })
            .GroupBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ticker)
            .ThenBy(x => x.Network)
            .ToList();
    }

    // ── IExchangePriceApi (sell) ──────────────────────────────────────────────
    // SELL 1 XMR -> USDT: withdraw_amount is the USDT received = direct sell price.
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = query.Base.Ticker.Trim().ToUpperInvariant();
        var to = query.Quote.Ticker.Trim().ToUpperInvariant();

        if (await FetchRateAsync(from, to, SellProbeXmr, ct) is not { } rate
            || rate.Deposit <= 0 || rate.Withdraw <= 0) return null;

        var price = rate.Withdraw / rate.Deposit;   // USDT per 1 XMR
        if (price <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: price,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"sell {from}->{to} deposit={rate.Deposit} withdraw={rate.Withdraw}");
    }

    // ── IExchangeBuyPriceApi ──────────────────────────────────────────────────
    // BUY: send USDT, receive XMR. Price (USDT per 1 XMR) = depositUsdt / xmrReceived.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var xmr = query.Base.Ticker.Trim().ToUpperInvariant();
        var usdt = query.Quote.Ticker.Trim().ToUpperInvariant();
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;

        if (await FetchRateAsync(usdt, xmr, probe, ct) is not { } rate  // from USDT -> to XMR
            || rate.Deposit <= 0 || rate.Withdraw <= 0) return null;

        var price = rate.Deposit / rate.Withdraw;   // USDT spent per 1 XMR received
        if (price <= 0) return null;

        return new PriceResult(
            Exchange: ExchangeKey,
            Base: query.Base,
            Quote: query.Quote,
            Price: price,
            TimestampUtc: DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw: $"buy {usdt}->{xmr} deposit={rate.Deposit} withdraw={rate.Withdraw}");
    }

    // ── Rate fetch ────────────────────────────────────────────────────────────
    private async Task<RateRow?> FetchRateAsync(string from, string to, decimal depositAmount, CancellationToken ct)
    {
        var qs = $"rate/?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}" +
                 $"&deposit_amount={depositAmount.ToString("G", CultureInfo.InvariantCulture)}" +
                 $"&crypto_only=true";

        var body = await SendAsync(HttpMethod.Get, qs, ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        // Response is an array of RateV2 rows (occasionally nested one level deep).
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var row in Flatten(doc.RootElement))
            {
                var deposit = GetDecimal(row, "deposit_amount");
                var withdraw = GetDecimal(row, "withdraw_amount");
                if (withdraw is > 0)
                {
                    // deposit_amount may be echoed back; fall back to what we sent.
                    return new RateRow(deposit is > 0 ? deposit.Value : depositAmount, withdraw.Value);
                }
            }
        }
        catch { /* fall through */ }
        return null;
    }

    // The rate endpoint sometimes returns [[ {...}, {...} ]] instead of [ {...} ];
    // yield every object element regardless of nesting depth (one level).
    private static IEnumerable<JsonElement> Flatten(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var inner in el.EnumerateArray())
                    if (inner.ValueKind == JsonValueKind.Object) yield return inner;
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                yield return el;
            }
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private async Task<string?> SendAsync(HttpMethod method, string relativeUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 3, 30)));
        try
        {
            using var req = new HttpRequestMessage(method, relativeUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(opt.ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {opt.ApiKey}");
            if (!string.IsNullOrWhiteSpace(opt.ReferralToken))
                req.Headers.TryAddWithoutValidation("x-referral-token", opt.ReferralToken);
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                req.Headers.UserAgent.ParseAdd(opt.UserAgent);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch { return null; }
    }

    private static decimal? GetDecimal(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null,
            _ => null
        };
    }

    // ── DTOs ──────────────────────────────────────────────────────────────────
    private sealed class CurrencyDto
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("is_fiat")] public bool IsFiat { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("common_symbol")] public string? CommonSymbol { get; set; }
    }

    private readonly record struct RateRow(decimal Deposit, decimal Withdraw);
}
