using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Bitania instant-swap partner client. Docs: https://api.bitania.com/docs/
///
/// All swap endpoints are v2-HMAC signed (X-API-KEY/X-API-SIGN/X-API-TIMESTAMP/
/// X-API-NONCE). The signature is HMAC-SHA256 over the canonical string
///   "{timestamp}\n{METHOD}\n{path}\n{sha256_hex(body)}"
/// keyed by the API secret. See BitaniaOptions for header/path details.
///
/// POST /swap/price   body { type, from, to, direction, amount }
///   → { code, msg, data:{ from:{amount,rate,min,max,…}, to:{amount,…}, fee, slippage } }
///     direction="from": amount is what you SEND (from); data.to.amount is what you RECEIVE.
///
/// SELL (Base→Quote): from=Base, to=Quote, direction=from, amount=1
///   → data.to.amount = Quote received per 1 Base (direct sell price).
/// BUY  (Quote→Base): from=Quote, to=Base, direction=from, amount=probe (quote ccy)
///   → buyPrice = probe / data.to.amount  (Quote spent per 1 Base).
///
/// Bitania codes fold the network into one ticker (USDT→USDTTRC/USDTERC); see
/// Resolve(). Codes should be confirmed against POST /swap/currencies once keys land.
/// </summary>
public sealed class BitaniaClient : IBitaniaClient
{
    private readonly HttpClient _http;
    private readonly BitaniaOptions opt;
    private bool _warnedNoKey;

    public string ExchangeKey => "bitania";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public BitaniaClient(HttpClient http, IOptions<BitaniaOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── Asset resolution: AssetRef (ticker + optional network) → Bitania code ──
    private string Resolve(AssetRef a)
    {
        // If the resolver already matched this asset against the live /swap/currencies
        // list, AssetRef.ExchangeId holds Bitania's authoritative code — use it.
        if (!string.IsNullOrWhiteSpace(a.ExchangeId))
            return a.ExchangeId!.Trim();

        var ticker = (a.Ticker ?? "").Trim().ToUpperInvariant();
        var net = (a.Network ?? "").Trim().ToUpperInvariant();

        return ticker switch
        {
            "XMR" => opt.XmrCode,
            "BTC" => opt.BtcCode,
            "ETH" => opt.EthCode,
            "USDT" => net is "ETH" or "ERC" or "ERC20" or "ETHEREUM"
                ? opt.UsdtErcCode
                : opt.UsdtTrcCode, // default Tron (TRC20)
            _ => ticker,
        };
    }

    // ── SELL: Base → Quote (1 base → quote received) ──────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!HasCreds()) return null;

        var from = Resolve(query.Base);
        var to = Resolve(query.Quote);

        var (recv, min) = await PriceAsync(from, to, 1m, query.Fixed, ct);

        if (recv is null && min is > 0m)
        {
            var probe = min.Value * 1.1m;
            (recv, _) = await PriceAsync(from, to, probe, query.Fixed, ct);
            if (recv is null or <= 0m) return null;
            return MakeResult(query, recv.Value / probe); // quote per 1 base
        }

        if (recv is null or <= 0m) return null;
        return MakeResult(query, recv.Value); // amount=1 → direct
    }

    // ── BUY: Quote → Base (probe quote → base received) ───────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (!HasCreds()) return null;

        var baseCode = Resolve(query.Base);
        var quoteCode = Resolve(query.Quote);
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;

        var (recv, min) = await PriceAsync(quoteCode, baseCode, probe, query.Fixed, ct);

        if (recv is null && min is > 0m)
        {
            probe = min.Value * 1.1m;
            (recv, _) = await PriceAsync(quoteCode, baseCode, probe, query.Fixed, ct);
        }

        if (recv is null or <= 0m) return null;
        return MakeResult(query, probe / recv.Value); // quote spent per 1 base
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        if (!HasCreds()) return Array.Empty<ExchangeCurrency>();

        var body = await SignedPostAsync("/swap/currencies", "{}", ct);
        if (body is null) return Array.Empty<ExchangeCurrency>();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<ExchangeCurrency>();

            var list = new List<ExchangeCurrency>();
            foreach (var c in data.EnumerateArray())
            {
                var code = c.TryGetProperty("code", out var cd) ? cd.GetString() ?? "" : "";
                var coin = c.TryGetProperty("coin", out var co) ? co.GetString() ?? "" : "";
                var network = c.TryGetProperty("network", out var ne) ? ne.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(code)) code = coin;
                if (string.IsNullOrWhiteSpace(coin)) continue;
                // ExchangeId = Bitania's per-currency code (folds in network, e.g. USDTTRC);
                // Ticker/Network are the standard pair used by the resolver.
                list.Add(new ExchangeCurrency(code, coin, network));
            }
            return list;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITANIA] currencies parse error: {ex.Message}");
            return Array.Empty<ExchangeCurrency>();
        }
    }

    // ── Core price call ───────────────────────────────────────────────────────
    // Returns (receiveAmount, minAmount-on-from). On below-min returns (null, min).
    private async Task<(decimal? receive, decimal? min)> PriceAsync(
        string from, string to, decimal amount, bool isFixed, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = isFixed ? "fixed" : opt.RateType,
            from,
            to,
            direction = "from",
            amount
        });

        var body = await SignedPostAsync("/swap/price", payload, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // code != 0 → error (msg holds the reason)
            if (root.TryGetProperty("code", out var codeEl) &&
                codeEl.ValueKind == JsonValueKind.Number && codeEl.GetInt32() != 0)
            {
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() : null;
                Console.WriteLine($"[BITANIA] {from}->{to} error: {msg}");
                return (null, null);
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return (null, null);

            decimal? min = null;
            if (data.TryGetProperty("from", out var fromEl) && fromEl.ValueKind == JsonValueKind.Object &&
                fromEl.TryGetProperty("min", out var minEl))
            {
                var mv = ReadDecimal(minEl);
                if (mv > 0m) min = mv;
            }

            // Below-minimum is surfaced via data.errors (and a too-small/zero output).
            if (data.TryGetProperty("errors", out var errEl) && errEl.ValueKind == JsonValueKind.Array &&
                errEl.GetArrayLength() > 0)
            {
                Console.WriteLine($"[BITANIA] {from}->{to} warn: {errEl}");
                if (min is > 0m && amount < min) return (null, min);
            }

            if (data.TryGetProperty("to", out var toEl) && toEl.ValueKind == JsonValueKind.Object &&
                toEl.TryGetProperty("amount", out var amtEl))
            {
                var recv = ReadDecimal(amtEl);
                if (recv > 0m) return (recv, min);
            }

            Console.WriteLine($"[BITANIA] no to.amount in: {Trunc(body)}");
            return (null, min);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITANIA] parse error: {ex.Message} — {Trunc(body)}");
            return (null, null);
        }
    }

    // ── HMAC-signed POST ──────────────────────────────────────────────────────
    private async Task<string?> SignedPostAsync(string endpoint, string jsonBody, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}{endpoint}";
        var method = "POST";

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");
        var bodyHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(jsonBody)));
        var signPath = $"{opt.SignPathPrefix.TrimEnd('/')}{endpoint}";
        var canonical = $"{ts}\n{method}\n{signPath}\n{bodyHash}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(opt.ApiSecret));
        var sign = Hex(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("X-API-SIGN", sign);
            req.Headers.TryAddWithoutValidation("X-API-TIMESTAMP", ts);
            req.Headers.TryAddWithoutValidation("X-API-NONCE", nonce);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);
            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine("[BITANIA] Timed out");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"[BITANIA] HTTP {(int)resp.StatusCode}: {Trunc(body)}");
                return null;
            }
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITANIA] Error: {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private bool HasCreds()
    {
        if (!string.IsNullOrWhiteSpace(opt.ApiKey) && !string.IsNullOrWhiteSpace(opt.ApiSecret))
            return true;
        if (!_warnedNoKey)
        {
            Console.WriteLine("[BITANIA] missing ApiKey/ApiSecret — set Bitania:ApiKey and Bitania:ApiSecret in appsettings.json");
            _warnedNoKey = true;
        }
        return false;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Trunc(string s) => s.Length <= 300 ? s : s[..300];

    private static decimal ReadDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}
