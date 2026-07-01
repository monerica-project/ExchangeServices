using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// StereoSwap partner API client.
///
/// Auth: raw API key in "X-API-Key" header (no scheme prefix).
///       Configurable via StereoSwap:AuthHeaderName / StereoSwap:AuthScheme.
///
/// POST /partner/v1/exchange/calculate/
/// Body: { amount, last_source:"deposit", type_swap, mode:"standard",
///         from_coin, from_network, to_coin, to_network }
/// Response: { receive_amount, min_amount, max_amount, rate }
///   receive_amount = units of to_coin received for `amount` of from_coin
///
/// Network note: XMR<->USDT is NOT available on TRX. Use a supported USDT
/// network (ETH/BSC/MATIC/ARBITRUM/APT/...). USDT price is network-agnostic.
///
/// Pairs are derived from query.Base/query.Quote (XMR↔USDT/BTC/ETH). Each
/// AssetRef resolves to a (coin, network): explicit network wins, else the
/// native chain (XMR→XMR, BTC→BTC, ETH→ETH; bare USDT→opt.UsdtNetwork=ETH).
///
/// SELL (Base→Quote): from=Base, to=Quote, amount=1
///   → receive_amount = Quote per 1 Base (direct)
///
/// BUY  (Quote→Base): from=Quote, to=Base, amount=probe (query.ProbeAmount ?? default)
///   → receive_amount = Base received → buyPrice = probe / receive_amount
///
/// If amount < min_amount, retry at min_amount * 1.1
/// </summary>
public sealed class StereoSwapClient : IStereoSwapClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly StereoSwapOptions opt;

    public string ExchangeKey => "stereoswap";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public StereoSwapClient(HttpClient http, IOptions<StereoSwapOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── Asset resolution ─────────────────────────────────────────────────────
    // Map an AssetRef (ticker + optional network) to StereoSwap's (coin, network).
    // An explicit AssetRef.Network wins; a bare ticker resolves to its native chain
    // (XMR→XMR, BTC→BTC, ETH→ETH). USDT is network-agnostic for pricing but is NOT
    // available on TRC20, so a bare USDT defaults to opt.UsdtNetwork (ETH). XMR's
    // native chain comes from opt.XmrNetwork so the XMR↔USDT pair stays identical.
    private (string Coin, string Network) Resolve(AssetRef a)
    {
        var ticker = (a.Ticker ?? "").Trim().ToUpperInvariant();

        // USDT price is network-agnostic, and StereoSwap does NOT list XMR<->USDT on
        // Tron/TRC20 — only ETH/BSC/etc. So ALWAYS quote USDT on our configured network
        // (default ETH), even when the caller requests USDT-on-Tron. Otherwise a USDT:Tron
        // request returns no quote and StereoSwap silently drops off the USDT page.
        if (ticker == "USDT")
            return (ticker, string.IsNullOrWhiteSpace(opt.UsdtNetwork) ? "ETH" : opt.UsdtNetwork.Trim().ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(a.Network))
            return (ticker, a.Network!.Trim().ToUpperInvariant());

        var native = ticker switch
        {
            "XMR" => string.IsNullOrWhiteSpace(opt.XmrNetwork) ? "XMR" : opt.XmrNetwork.Trim().ToUpperInvariant(),
            _ => ticker, // BTC→BTC, ETH→ETH, … native chain == ticker
        };
        return (ticker, native);
    }

    // ── SELL: Base → Quote (1 base → quote received) ──────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            ExchangeLog.Debug("[STEREOSWAP] missing ApiKey — set StereoSwap:ApiKey in appsettings.json");
            return null;
        }

        var baseA = Resolve(query.Base);
        var quoteA = Resolve(query.Quote);

        var (receiveAmt, minAmt) = await CalculateAsync(
            fromCoin: baseA.Coin, fromNetwork: baseA.Network,
            toCoin: quoteA.Coin, toNetwork: quoteA.Network,
            amount: 1m, ct);

        if (receiveAmt is null && minAmt is > 0m)
        {
            var probe = minAmt.Value * 1.1m;
            (receiveAmt, _) = await CalculateAsync(
                baseA.Coin, baseA.Network,
                quoteA.Coin, quoteA.Network,
                probe, ct);
            if (receiveAmt is null or <= 0m) return null;
            return MakeResult(query, receiveAmt.Value / probe); // quote received per 1 base
        }

        if (receiveAmt is null or <= 0m) return null;
        return MakeResult(query, receiveAmt.Value); // amount=1 → direct
    }

    // ── BUY: Quote → Base (probe quote → base received) ───────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            ExchangeLog.Debug("[STEREOSWAP] missing ApiKey — set StereoSwap:ApiKey in appsettings.json");
            return null;
        }

        var baseA = Resolve(query.Base);
        var quoteA = Resolve(query.Quote);

        // Probe is denominated in the QUOTE currency (PriceService sets it per quote).
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;

        var (receiveAmt, minAmt) = await CalculateAsync(
            fromCoin: quoteA.Coin, fromNetwork: quoteA.Network,
            toCoin: baseA.Coin, toNetwork: baseA.Network,
            amount: probe, ct);

        if (receiveAmt is null && minAmt is > 0m)
        {
            probe = minAmt.Value * 1.1m;
            (receiveAmt, _) = await CalculateAsync(
                quoteA.Coin, quoteA.Network,
                baseA.Coin, baseA.Network,
                probe, ct);
        }

        if (receiveAmt is null or <= 0m) return null;
        return MakeResult(query, probe / receiveAmt.Value); // quote spent per 1 base
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core calculate call ───────────────────────────────────────────────────

    private async Task<(decimal? receiveAmount, decimal? minAmount)> CalculateAsync(
        string fromCoin, string fromNetwork,
        string toCoin, string toNetwork,
        decimal amount, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            amount,
            last_source = "deposit",
            type_swap = opt.TypeSwap,
            mode = opt.Mode,
            from_coin = fromCoin,
            from_network = fromNetwork,
            to_coin = toCoin,
            to_network = toNetwork
        });

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/partner/v1/exchange/calculate/";
        var body = await PostAsync(fullUrl, payload, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var minAmount = root.TryGetProperty("min_amount", out var minEl) ? ReadDecimal(minEl) : 0m;
            if (minAmount <= 0m) minAmount = 0m;

            // If below minimum the API likely returns receive_amount=0 or an error
            if (minAmount > 0m && amount < minAmount)
            {
                ExchangeLog.Debug($"[STEREOSWAP] below min {minAmount} ({fromCoin}→{toCoin})");
                return (null, minAmount);
            }

            if (root.TryGetProperty("receive_amount", out var raEl))
            {
                var v = ReadDecimal(raEl);
                if (v > 0m) return (v, minAmount > 0m ? minAmount : null);
            }

            ExchangeLog.Debug($"[STEREOSWAP] no receive_amount in: {body}");
            return (null, minAmount > 0m ? minAmount : null);
        }
        catch (Exception ex)
        {
            ExchangeLog.Debug($"[STEREOSWAP] parse error: {ex.Message} — {body}");
            return (null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> PostAsync(string fullUrl, string jsonPayload, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        var headerName = string.IsNullOrWhiteSpace(opt.AuthHeaderName) ? "X-API-Key" : opt.AuthHeaderName.Trim();
        var headerValue = string.IsNullOrWhiteSpace(opt.AuthScheme)
            ? opt.ApiKey
            : $"{opt.AuthScheme.Trim()} {opt.ApiKey}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            req.Headers.TryAddWithoutValidation(headerName, headerValue);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                ExchangeLog.Debug("[STEREOSWAP] Timed out");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            if (!resp.IsSuccessStatusCode)
            {
                ExchangeLog.Debug($"[STEREOSWAP] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");
                return null;
            }

            return body;
        }
        catch (Exception ex)
        {
            ExchangeLog.Debug($"[STEREOSWAP] Error: {ex.Message}");
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

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}