using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// ChangeHero API client (JSON-RPC 2.0).
///
/// Endpoint: POST https://api.changehero.io/v2/
/// Auth headers required on every request:
///   api-key: your API key
///   sign:    HMAC-SHA512 of the full JSON request body, keyed with your secret
///
/// SELL (XMR→USDT): getExchangeAmount { from=xmr, to=usdttrc20, amount=1 }
///   → result = USDT per 1 XMR = sell price directly
///
/// BUY (USDT→XMR): getExchangeAmount { from=usdttrc20, to=xmr, amount=probe }
///   → result = XMR received → buyPrice = probe / result
/// </summary>
public sealed class ChangeHeroClient : IChangeHeroClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly ChangeHeroOptions opt;

    public string ExchangeKey => "changehero";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public ChangeHeroClient(HttpClient http, IOptions<ChangeHeroOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // Resolve an asset to ChangeHero's internal code. PriceService resolves the
    // exact code into AssetRef.ExchangeId (from getCurrenciesFull) — use it when
    // present; otherwise fall back to the configured XMR/USDT codes or the lowercased
    // ticker. This lets the client price ANY pair (XMR/USDT, XMR/BTC, XMR/ETH, …)
    // rather than being hardcoded to XMR/USDT.
    private string Code(AssetRef a)
    {
        if (!string.IsNullOrWhiteSpace(a.ExchangeId)) return a.ExchangeId!.Trim().ToLowerInvariant();
        var t = (a.Ticker ?? "").Trim().ToUpperInvariant();
        if (t == "XMR") return opt.XmrCode;
        if (t == "USDT") return opt.UsdtCode;
        return t.ToLowerInvariant();
    }

    // ── SELL: 1 Base → Quote (e.g. XMR → USDT/BTC/ETH) ───────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = Code(query.Base);
        var to = Code(query.Quote);

        var result = await GetExchangeAmountAsync(from, to, 1m, ct);
        if (result is > 0m)
            return MakeResult(query, result.Value);

        // amount=1 may be below minimum — fetch min and retry
        var min = await GetMinAmountAsync(from, to, ct);
        if (min is null or <= 0m) return null;

        var probe = min.Value * 1.1m;
        var result2 = await GetExchangeAmountAsync(from, to, probe, ct);
        if (result2 is null or <= 0m) return null;

        return MakeResult(query, result2.Value / probe);
    }

    // ── BUY: Quote → Base (e.g. USDT → XMR) ──────────────────────────────────
    // NB: ChangeHero has XMR outbound disabled (enabledTo=false), so any *->XMR
    // quote is rejected — i.e. you cannot BUY Monero there. This returns null for
    // XMR (and any other receive-disabled asset); it works for normal coins.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = Code(query.Quote);   // spend the quote
        var to = Code(query.Base);      // receive the base
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;

        var result = await GetExchangeAmountAsync(from, to, probe, ct);

        if (result is null or <= 0m)
        {
            var min = await GetMinAmountAsync(from, to, ct);
            if (min is null or <= 0m) return null;
            probe = min.Value * 1.1m;
            result = await GetExchangeAmountAsync(from, to, probe, ct);
            if (result is null or <= 0m) return null;
        }

        return MakeResult(query, probe / result.Value);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    // getCurrenciesFull returns [{ name, publicTicker, enabled, protocol, blockchain, ... }].
    // `name` is ChangeHero's internal code (e.g. "adabsc"); `publicTicker` is the
    // standard symbol (e.g. "ADA"); `protocol` is the network (ERC20, TRC20, XMR...).
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var body = await RpcAsync("getCurrenciesFull", new { }, ct);
        if (body is null) return Array.Empty<ExchangeCurrency>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False)
                    continue;

                var code = GetStr(el, "name");
                var ticker = GetStr(el, "publicTicker") ?? code;
                if (string.IsNullOrWhiteSpace(ticker)) continue;

                var network = GetStr(el, "protocol");
                if (string.IsNullOrWhiteSpace(network)) network = GetStr(el, "blockchain") ?? "";

                list.Add(new ExchangeCurrency(
                    ExchangeId: (code ?? ticker).Trim().ToLowerInvariant(),
                    Ticker: ticker.Trim().ToUpperInvariant(),
                    Network: network.Trim()));
            }

            return list
                .GroupBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(x => x.Ticker)
                .ThenBy(x => x.Network)
                .ToList();
        }
        catch { return Array.Empty<ExchangeCurrency>(); }
    }

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ── RPC helpers ───────────────────────────────────────────────────────────

    private async Task<decimal?> GetExchangeAmountAsync(
        string from, string to, decimal amount, CancellationToken ct)
    {
        var body = await RpcAsync("getExchangeAmount", new
        {
            from,
            to,
            amount = amount.ToString(CultureInfo.InvariantCulture)
        }, ct);

        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
            {
                Console.WriteLine($"[CHANGEHERO] error: {err}");
                return null;
            }
            if (root.TryGetProperty("result", out var res))
            {
                var v = ParseDecimal(res);
                if (v > 0m) return v;
            }
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHANGEHERO] parse error: {ex.Message} — {body}");
            return null;
        }
    }

    private async Task<decimal?> GetMinAmountAsync(string from, string to, CancellationToken ct)
    {
        var body = await RpcAsync("getMinAmount", new { from, to }, ct);
        if (body is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("result", out var res))
            {
                var v = ParseDecimal(res);
                if (v > 0m) return v;
            }
            return null;
        }
        catch { return null; }
    }

    // ── HTTP / RPC ────────────────────────────────────────────────────────────

    private static int _rpcId = 0;

    private async Task<string?> RpcAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _rpcId).ToString();
        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        });

        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, opt.BaseUrl);
            req.Headers.TryAddWithoutValidation("api-key", opt.ApiKey);
            // The read methods we use (getCurrenciesFull, getExchangeAmount, getMinAmount)
            // authenticate with the api-key alone. Only sign when a secret is configured —
            // a wrong/empty signature would otherwise be rejected.
            if (!string.IsNullOrWhiteSpace(opt.ApiSecret))
                req.Headers.TryAddWithoutValidation("sign", SignPayload(payload, opt.ApiSecret));
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", opt.UserAgent);
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[CHANGEHERO] Timed out: {method}");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[CHANGEHERO] {method} HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CHANGEHERO] Error ({method}): {ex.Message}");
            return null;
        }
    }

    private static string SignPayload(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA512(keyBytes);
        var hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static decimal ParseDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}