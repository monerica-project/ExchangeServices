using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ExchangeServices.Implementations;

/// <summary>
/// Exwell business API client. https://exwell.io/docs/businessApi
///
/// Endpoints (every request needs ?key=&lt;apiKey&gt;):
///   GET /currencies               → { "CODE": { coinName, network, available, minamount, maxamount, tagname }, ... }
///   GET /rate?from=&to=&amount=    → { result, rate, withdrawalFee, minamount, maxamount }
///
/// NOTE: untested — Exwell requires a valid key for ALL endpoints (even /currencies),
/// so this is wired per the docs but not verified live. In particular the meaning of
/// the `rate` field (output amount vs per-unit) must be confirmed once a key exists;
/// here it's treated as the amount of `to` received for the given input `amount`
/// (the common convention across the other clients).
/// </summary>
public sealed class ExwellClient : IExwellClient
{
    private readonly HttpClient _http;
    private readonly ExwellOptions opt;

    public string ExchangeKey => "exwell";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    private const decimal SellProbeXmr = 1m;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public ExwellClient(HttpClient http, IOptions<ExwellOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    // /currencies returns an object keyed by currency code. Keep only available ones.
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return Array.Empty<ExchangeCurrency>();

        var body = await SendAsync($"currencies?key={Uri.EscapeDataString(opt.ApiKey)}", ct);
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<ExchangeCurrency>();

        Dictionary<string, CurrencyDto>? map;
        try { map = JsonSerializer.Deserialize<Dictionary<string, CurrencyDto>>(body, JsonOpts); }
        catch { return Array.Empty<ExchangeCurrency>(); }
        if (map is null || map.Count == 0) return Array.Empty<ExchangeCurrency>();

        return map
            .Where(kv => kv.Value is not null && kv.Value!.Available && !string.IsNullOrWhiteSpace(kv.Key))
            .Select(kv =>
            {
                var ticker = kv.Key.Trim().ToUpperInvariant();
                var network = (kv.Value!.Network ?? "").Trim();
                return new ExchangeCurrency(
                    ExchangeId: $"{ticker}|{network}".ToLowerInvariant(),
                    Ticker: ticker,
                    Network: network);
            })
            .GroupBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ticker)
            .ThenBy(x => x.Network)
            .ToList();
    }

    // ── SELL: Base → Quote (e.g. XMR → USDT) ─────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = Code(query.Base);
        var to = Code(query.Quote);

        var rate = await GetRateAsync(from, to, SellProbeXmr, ct);
        if (rate is null or <= 0) return null;

        // rate = `to` received for SellProbeXmr of `from`; price = received / sent.
        var price = rate.Value / SellProbeXmr;
        return price <= 0 ? null : MakeResult(query, price);
    }

    // ── BUY: Quote → Base (e.g. USDT → XMR) ──────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = Code(query.Quote);
        var to = Code(query.Base);
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;

        var rate = await GetRateAsync(from, to, probe, ct);
        if (rate is null or <= 0) return null;

        // rate = base received for `probe` of quote; price (quote per base) = probe / received.
        var price = probe / rate.Value;
        return price <= 0 ? null : MakeResult(query, price);
    }

    private string Code(AssetRef a)
    {
        if (!string.IsNullOrWhiteSpace(a.ExchangeId)) return a.ExchangeId!.Trim().ToUpperInvariant();
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        if (t == "XMR") return opt.XmrCode;
        if (t == "USDT") return opt.UsdtCode;
        return t;
    }

    private async Task<decimal?> GetRateAsync(string from, string to, decimal amount, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        var url = $"rate?key={Uri.EscapeDataString(opt.ApiKey)}" +
                  $"&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}" +
                  $"&amount={amount.ToString("G", CultureInfo.InvariantCulture)}";

        var body = await SendAsync(url, ct);
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.False) return null;
            return GetDecimal(root, "rate");
        }
        catch { return null; }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private async Task<string?> SendAsync(string relativeUrl, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.TimeoutSeconds, 3, 30)));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
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

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);

    // ── DTO ───────────────────────────────────────────────────────────────────
    private sealed class CurrencyDto
    {
        public string? CoinName { get; set; }
        public string? Network { get; set; }
        public string? TagName { get; set; }
        public bool Available { get; set; }
    }
}
