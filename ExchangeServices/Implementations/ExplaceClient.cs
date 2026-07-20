using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ExchangeServices.Implementations;

/// <summary>
/// Explace instant-swap client (https://explace.io).
///
/// Endpoints (base = https://api.explace.io/):
///   POST protected/swap/estimate  { deposit:{coin,network,amount,provider}, withdraw:{coin,network,provider}, details:{type} }
///                                  → { deposit:{amount,…}, withdraw:{amount,fee,…}, details:{rate} }
/// Auth: <c>X-API-KEY</c> header on every /protected request. No key → 401.
///
/// SELL (per-XMR price): deposit=xmr amount=1, withdraw=quote → Price = withdraw.amount / deposit.amount (quote per 1 XMR).
/// BUY: deposit=quote amount=probe, withdraw=xmr → Price = deposit.amount / withdraw.amount (quote needed per 1 XMR).
/// Rate is computed from the response's own amounts (net of the withdrawal fee), so it is correct
/// regardless of the probe size — but callers must probe a REALISTIC amount, not the pair minimum.
/// provider is left null so Explace auto-routes to the best venue.
/// </summary>
public sealed class ExplaceClient : IExplaceClient
{
    private readonly HttpClient http;
    private readonly ExplaceOptions opt;

    public string ExchangeKey => "explace";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;   // "V" — auto-routed, venue varies per quote
    public decimal MinAmountUsd => opt.MinAmountUsd;

    // Explace estimates both float and fixed; the site resolves one rate type per exchange at startup.
    public string RateType =>
        (opt.RateType.Equals("fix", StringComparison.OrdinalIgnoreCase) ||
         opt.RateType.Equals("fixed", StringComparison.OrdinalIgnoreCase))
            ? RateTypes.Fixed
            : RateTypes.Float;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ExplaceClient(HttpClient http, IOptions<ExplaceOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ── IExchangePriceApi: SELL (quote received for 1 XMR) ────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey)) return null;

        // MoneroPriceNow leaves ProbeAmount null (unit XMR sell); SwapRaven passes the real
        // trade size so the estimate reflects that amount. Fall back to the XMR unit probe.
        var probe = query.ProbeAmount is decimal pa && pa > 0 ? pa : opt.SellProbeXmr;

        var dto = await EstimateAsync(query.Base, query.Quote, probe, query.Fixed, ct);
        if (dto is null || dto.AmountFrom <= 0m || dto.AmountTo <= 0m) return null;

        // Per-XMR quote = quote received / XMR sent, from the response's own amounts.
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
        var probe = query.ProbeAmount is decimal p && p > 0 ? p : opt.DefaultBuyProbeUsdt;

        var dto = await EstimateAsync(query.Quote, query.Base, probe, query.Fixed, ct);
        if (dto is null || dto.AmountFrom <= 0m || dto.AmountTo <= 0m) return null;

        // Quote needed per 1 XMR = quote sent / XMR received, from the response's own amounts.
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

    // ── estimate ────────────────────────────────────────────────────────────────
    // deposit = `from`, withdraw = `to`. Returns the response's own amountFrom/amountTo
    // (net of the withdrawal fee). A below-min / bad-pair request comes back HTTP 400 with an
    // error body → null (the row simply drops off the board for that pair).
    private async Task<AmountsDto?> EstimateAsync(
        AssetRef from, AssetRef to, decimal amount, bool fixedRate, CancellationToken ct)
    {
        var fromCoin = Coin(from);
        var toCoin = Coin(to);
        if (fromCoin.Length == 0 || toCoin.Length == 0) return null;

        var type = fixedRate
            ? "fix"
            : (opt.RateType.Equals("fixed", StringComparison.OrdinalIgnoreCase) ? "fix" : opt.RateType);

        var body =
            "{\"deposit\":{\"coin\":\"" + fromCoin + "\",\"network\":\"" + Network(from) + "\"," +
            "\"amount\":" + amount.ToString(CultureInfo.InvariantCulture) + ",\"provider\":null}," +
            "\"withdraw\":{\"coin\":\"" + toCoin + "\",\"network\":\"" + Network(to) + "\",\"provider\":null}," +
            "\"details\":{\"type\":\"" + type + "\"}}";

        var (status, resp) = await SendAsync(HttpMethod.Post, "protected/swap/estimate", body, ct);
        if (status is null || !IsSuccess(status.Value) || string.IsNullOrWhiteSpace(resp)) return null;

        try
        {
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var amountFrom = GetNested(root, "deposit", "amount");
            var amountTo = GetNested(root, "withdraw", "amount");
            if (amountTo <= 0m || amountFrom <= 0m) return null;

            return new AmountsDto { AmountFrom = amountFrom, AmountTo = amountTo };
        }
        catch
        {
            return null;
        }
    }

    // Explace coin code == the ticker (upper-case).
    private static string Coin(AssetRef a) => (a.Ticker ?? string.Empty).Trim().ToUpperInvariant();

    // Explace network code. Main-chain coins (XMR/BTC/ETH/…) use the ticker as the network.
    // The site labels USDT chains with human names ("Tron", "Ethereum", …) — map those to codes.
    private static string Network(AssetRef a)
    {
        var n = (a.Network ?? string.Empty).Trim();
        if (n.Length == 0)
        {
            // No network specified (SwapRaven passes bare tickers). USDT has no native chain —
            // default it to Tron (TRX), Explace's most common USDT network; native coins
            // (XMR/BTC/ETH/LTC/…) use the ticker as the network code.
            var ticker = (a.Ticker ?? string.Empty).Trim().ToUpperInvariant();
            return ticker == "USDT" ? "TRX" : ticker;
        }

        return n.ToLowerInvariant() switch
        {
            "tron" or "trc20" or "trc" => "TRX",
            "ethereum" or "erc20" or "erc" => "ETH",
            "solana" => "SOL",
            "binance smart chain" or "bsc" or "bep20" => "BSC",
            _ => n.ToUpperInvariant(),
        };
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private async Task<(HttpStatusCode? Status, string? Body)> SendAsync(
        HttpMethod method, string relative, string? jsonBody, CancellationToken ct)
    {
        var attempts = Math.Max(1, Math.Clamp(opt.RetryCount, 0, 5) + 1);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(method, relative);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
            if (!string.IsNullOrWhiteSpace(opt.UserAgent) && !req.Headers.UserAgent.Any())
                req.Headers.UserAgent.ParseAdd(opt.UserAgent);
            if (jsonBody is not null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

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

    // Only transient failures are worth a retry. A 400 (below-min / bad pair) is deterministic.
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

    private static decimal GetNested(JsonElement root, string obj, string name)
    {
        if (!root.TryGetProperty(obj, out var o) || o.ValueKind != JsonValueKind.Object) return 0m;
        if (!o.TryGetProperty(name, out var p)) return 0m;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            _ => 0m
        };
    }

    private sealed class AmountsDto
    {
        public decimal AmountFrom { get; set; }
        public decimal AmountTo { get; set; }
    }
}
