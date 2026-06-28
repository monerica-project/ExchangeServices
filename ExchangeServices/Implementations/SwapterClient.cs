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
/// Swapter.io API v2 client.
///
/// Auth: X-API-KEY header on estimate and min-amount endpoints.
///
/// SELL (XMR→USDT/TRC20):
///   POST /v2/swap/estimate { deposit:{coin:XMR,network:XMR,amount:1}, withdraw:{coin:USDT,network:TRC20} }
///   → withdraw.amount = USDT received per 1 XMR = sell price directly
///
/// BUY (USDT→XMR):
///   POST /v2/swap/estimate { deposit:{coin:USDT,network:TRC20,amount:probe}, withdraw:{coin:XMR,network:XMR} }
///   → withdraw.amount = XMR received → buyPrice = probe / withdraw.amount
///
/// MinAmountUsd:
///   Sell: minXmr (from /v2/swap/min-amount) × sellPrice = USD minimum
///   Buy:  minUsdt (from /v2/swap/min-amount) = USD minimum directly
///
/// If amount is below minimum, POST /v2/swap/min-amount to get the floor and retry.
/// </summary>
public sealed class SwapterClient : ISwapterClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly SwapterOptions opt;

    public string ExchangeKey => "swapter";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public SwapterClient(HttpClient http, IOptions<SwapterOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── Asset resolution ──────────────────────────────────────────────────────
    // Map an AssetRef (ticker + optional network) to Swapter's (coin, network).
    // Swapter uses the chain ticker as the network (XMR/BTC/ETH; USDT→TRX), not the
    // protocol name. A bare ticker resolves to its native chain; USDT defaults to
    // the configured stable-coin network (TRX). This keeps XMR/USDT identical to
    // the previous hard-coded behaviour while generalising to BTC/ETH and beyond.
    private (string Coin, string Network) Resolve(AssetRef a)
    {
        var coin = (a.Ticker ?? "").Trim().ToUpperInvariant();
        var net = (a.Network ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(net))
            return (coin, NormNet(net));

        var network = coin switch
        {
            "USDT" => opt.UsdtNetwork,   // stable-coin default chain (TRX)
            _ => coin,                    // XMR/BTC/ETH … use their own ticker as native network
        };
        return (coin, network);
    }

    // Normalise common protocol/chain aliases to Swapter's chain-ticker network strings.
    private static string NormNet(string n) => n.Trim().ToUpperInvariant() switch
    {
        "TRON" or "TRC20" or "TRX" => "TRX",
        "ETHEREUM" or "ERC20" or "ETH" => "ETH",
        "BITCOIN" or "BTC" => "BTC",
        "MONERO" or "XMR" => "XMR",
        var x => x,
    };

    // ── SELL: Base → Quote (XMR → USDT/BTC/ETH) ──────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (baseCoin, baseNet) = Resolve(query.Base);
        var (quoteCoin, quoteNet) = Resolve(query.Quote);

        var min = await GetMinAmountAsync(baseCoin, baseNet, quoteCoin, quoteNet, ct);
        var probe = (min is > 0m && min > 1m) ? min.Value * 1.1m : 1m;

        var (withdrawAmt, _) = await EstimateAsync(
            baseCoin, baseNet, probe,
            quoteCoin, quoteNet, ct);

        if (withdrawAmt is null or <= 0m) return null;

        // withdrawAmt = quote received for probe base → quote received per 1 base
        var sellPrice = withdrawAmt.Value / probe;

        // min is in the base currency → convert to quote using sell price (USD when quote is USDT)
        decimal? minUsd = min is > 0m ? min.Value * sellPrice : null;
        minUsd ??= opt.MinAmountUsd > 0m ? opt.MinAmountUsd : null;

        return MakeResult(query, sellPrice, minUsd);
    }

    // ── BUY: Quote → Base (USDT/BTC/ETH → XMR) ───────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (baseCoin, baseNet) = Resolve(query.Base);
        var (quoteCoin, quoteNet) = Resolve(query.Quote);

        var min = await GetMinAmountAsync(quoteCoin, quoteNet, baseCoin, baseNet, ct);
        // Probe is in the QUOTE currency (PriceService sets it per quote; default USDT).
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;
        if (min is > 0m && min > probe) probe = min.Value * 1.1m;

        var (withdrawAmt, _) = await EstimateAsync(
            quoteCoin, quoteNet, probe,
            baseCoin, baseNet, ct);

        if (withdrawAmt is null or <= 0m) return null;

        // withdrawAmt = base received for probe quote → quote spent per 1 base
        // min is in the quote currency (= USD when quote is USDT)
        decimal? minUsd = min is > 0m ? min : null;
        minUsd ??= opt.MinAmountUsd > 0m ? opt.MinAmountUsd : null;

        return MakeResult(query, probe / withdrawAmt.Value, minUsd);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExchangeCurrency>>(Array.Empty<ExchangeCurrency>());

    // ── Core estimate ─────────────────────────────────────────────────────────
    private async Task<(decimal? withdrawAmount, decimal? minimumRequired)> EstimateAsync(
        string depositCoin, string depositNetwork, decimal depositAmount,
        string withdrawCoin, string withdrawNetwork,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            deposit = new { coin = depositCoin, network = depositNetwork, amount = depositAmount },
            withdraw = new { coin = withdrawCoin, network = withdrawNetwork },
            type = "float"
        }, JsonOpt);

        var body = await PostAsync("v2/swap/estimate", payload, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("withdraw", out var wd) &&
                wd.TryGetProperty("amount", out var amtEl))
            {
                var v = ReadDecimal(amtEl);
                if (v > 0m) return (v, null);
            }

            Console.WriteLine($"[SWAPTER] estimate below min or bad response: {body}");
            var min = await GetMinAmountAsync(depositCoin, depositNetwork, withdrawCoin, withdrawNetwork, ct);
            return (null, min);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SWAPTER] parse error: {ex.Message} — {body}");
            return (null, null);
        }
    }

    private async Task<decimal?> GetMinAmountAsync(
        string depositCoin, string depositNetwork,
        string withdrawCoin, string withdrawNetwork,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            deposit = new { coin = depositCoin, network = depositNetwork },
            withdraw = new { coin = withdrawCoin, network = withdrawNetwork }
        }, JsonOpt);

        var body = await PostAsync("v2/swap/min-amount", payload, ct);
        if (body is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("amount", out var el))
            {
                var v = ReadDecimal(el);
                if (v > 0m) return v;
            }
            return null;
        }
        catch { return null; }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<string?> PostAsync(string relativeUrl, string jsonPayload, CancellationToken ct)
    {
        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/{relativeUrl.TrimStart('/')}";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        Console.WriteLine($"[SWAPTER] POST {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            req.Headers.TryAddWithoutValidation("X-API-KEY", opt.ApiKey);
            req.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[SWAPTER] Timed out: {fullUrl}");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[SWAPTER] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SWAPTER] Error: {fullUrl} — {ex.Message}");
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

    private static PriceResult MakeResult(PriceQuery q, decimal price, decimal? minAmountUsd = null) =>
        new(q.Base.ToString(), q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null, minAmountUsd);
}