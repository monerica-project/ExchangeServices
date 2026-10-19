using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ExchangeServices.Implementations;

/// <summary>
/// AnonChange partner API client (https://anonchange.com/docs).
///
/// POST /api/partner/v1/price { from, to, rateType, amount } →
///   { amountIn, amountOut, rate, feePercent, networkFee, minDeposit, maxDeposit, ... }
/// amountOut is what the customer receives after fees, so the unit rate is computed from
/// amountIn/amountOut (decimal strings).
///
/// Auth: every request is signed. Headers X-API-KEY, X-API-TIMESTAMP (unix seconds),
/// X-API-NONCE (single use), X-API-SIGN = hex HMAC-SHA256(secret,
/// "{timestamp}\nPOST\n{path}\n{sha256hex(body)}"). Retries need a fresh nonce + timestamp.
///
/// AnonChange pays out from its own reserves: a quote whose amount is above maxDeposit can't
/// actually be filled, so it's treated as unavailable (null) rather than shown.
/// USDT is only on Ethereum/Solana (no Tron), so the XMR/USDT:Tron board gets no quote.
/// </summary>
public sealed class AnonChangeClient : IAnonChangeClient
{
    private readonly HttpClient http;
    private readonly AnonChangeOptions opt;

    public string ExchangeKey => "anonchange";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public string RateType =>
        opt.RateType.Equals("fixed", StringComparison.OrdinalIgnoreCase) ? RateTypes.Fixed : RateTypes.Float;

    public AnonChangeClient(HttpClient http, IOptions<AnonChangeOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // ── SELL: quote received for 1 XMR ─────────────────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = query.ProbeAmount is decimal pa && pa > 0 ? pa : opt.SellProbeXmr;

        var q = await PriceAsync(query.Base, query.Quote, probe, query.Fixed, ct);
        if (q is null) return null;

        var unitRate = q.Value.AmountOut / q.Value.AmountIn;
        return unitRate > 0m ? Result(query, unitRate) : null;
    }

    // ── BUY: quote needed to receive 1 XMR ─────────────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var probe = ProbeSizing.BuyProbe(query, opt.DefaultBuyProbeUsdt);

        var q = await PriceAsync(query.Quote, query.Base, probe, query.Fixed, ct);
        if (q is null) return null;

        var quotePerXmr = q.Value.AmountIn / q.Value.AmountOut;
        return quotePerXmr > 0m ? Result(query, quotePerXmr) : null;
    }

    private PriceResult Result(PriceQuery query, decimal price) => new(
        Exchange: ExchangeKey,
        Base: query.Base,
        Quote: query.Quote,
        Price: price,
        TimestampUtc: DateTimeOffset.UtcNow,
        CorrelationId: null,
        Raw: null);

    // ── price ─────────────────────────────────────────────────────────────────
    private async Task<(decimal AmountIn, decimal AmountOut)?> PriceAsync(
        AssetRef from, AssetRef to, decimal amount, bool fixedRate, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey) || string.IsNullOrWhiteSpace(opt.ApiSecret)) return null;

        var fromCoin = Coin(from);
        var toCoin = Coin(to);
        if (fromCoin.Length == 0 || toCoin.Length == 0 || fromCoin == toCoin || amount <= 0m) return null;

        var rateType = fixedRate || RateType == RateTypes.Fixed ? "fixed" : "float";
        var amountText = Math.Round(amount, 8).ToString("0.########", CultureInfo.InvariantCulture);

        var body = JsonSerializer.Serialize(new { from = fromCoin, to = toCoin, rateType, amount = amountText });

        var (status, resp) = await SendAsync("price", body, ct);
        if (status is null || (int)status.Value is < 200 or >= 300 || string.IsNullOrWhiteSpace(resp)) return null;

        try
        {
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var amountIn = Dec(root, "amountIn");
            var amountOut = Dec(root, "amountOut");
            if (amountIn <= 0m || amountOut <= 0m) return null;

            // Above what AnonChange can pay out right now → not a real, fillable quote.
            var maxDeposit = Dec(root, "maxDeposit");
            if (maxDeposit > 0m && amountIn > maxDeposit) return null;

            return (amountIn, amountOut);
        }
        catch
        {
            return null;
        }
    }

    // AnonChange coin codes: native tickers; USDT is per chain (USDT_ETH, USDT_SOL). No Tron.
    private static string Coin(AssetRef a)
    {
        var ticker = (a.Ticker ?? string.Empty).Trim().ToUpperInvariant();
        if (ticker is not ("USDT" or "USDC" or "DAI")) return ticker;

        var n = (a.Network ?? string.Empty).Trim().ToLowerInvariant();
        return n switch
        {
            "" or "ethereum" or "erc20" or "erc" or "eth" => ticker + "_ETH",
            "solana" or "sol" when ticker == "USDT" => "USDT_SOL",
            _ => string.Empty, // Tron/BSC/… not offered
        };
    }

    // ── signed HTTP ───────────────────────────────────────────────────────────
    private async Task<(HttpStatusCode? Status, string? Body)> SendAsync(string endpoint, string jsonBody, CancellationToken ct)
    {
        var attempts = Math.Max(1, Math.Clamp(opt.RetryCount, 0, 5) + 1);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));
        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        var bodyHash = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        var path = (http.BaseAddress?.AbsolutePath ?? "/api/partner/v1/").TrimEnd('/') + "/" + endpoint;
        var secret = Encoding.UTF8.GetBytes(opt.ApiSecret);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Fresh timestamp + nonce on every attempt (a nonce works once).
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var nonce = Guid.NewGuid().ToString("N");
            var signed = $"{ts}\nPOST\n{path}\n{bodyHash}";
            var sign = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(signed))).ToLowerInvariant();

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("X-API-TIMESTAMP", ts);
            req.Headers.TryAddWithoutValidation("X-API-NONCE", nonce);
            req.Headers.TryAddWithoutValidation("X-API-SIGN", sign);
            if (!string.IsNullOrWhiteSpace(opt.UserAgent) && !req.Headers.UserAgent.Any())
                req.Headers.UserAgent.ParseAdd(opt.UserAgent);
            var content = new ByteArrayContent(bodyBytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            req.Content = content;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            HttpResponseMessage? resp = null;
            try
            {
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var body = await resp.Content.ReadAsStringAsync(ct);
                var n = (int)resp.StatusCode;
                if ((n == 429 || n >= 500) && attempt < attempts - 1)
                {
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

    private static Task BackoffAsync(int attempt, CancellationToken ct)
        => Task.Delay(Math.Min(2000, (int)(250 * Math.Pow(2, attempt))) + Random.Shared.Next(0, 200), ct);

    private static decimal Dec(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var p)) return 0m;
        return p.ValueKind switch
        {
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : 0m,
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : 0m,
            _ => 0m,
        };
    }
}
