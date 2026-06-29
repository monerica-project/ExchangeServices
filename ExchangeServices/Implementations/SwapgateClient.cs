using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Swapgate.io client.
///
/// Rate endpoint (no auth required):
///   GET /api/v1/rates/public/one
///     ?instrumentFromCurrencyTitle=XMR
///     &instrumentFromNetworkTitle=XMR
///     &instrumentToCurrencyTitle=USDT
///     &instrumentToNetworkTitle=TRC20
///     &rateMode=FLOATING
///     &claimedDepositAmount=1
///     &markup=0
///
///   response.amountToGet = units of instrumentTo received for claimedDepositAmount of instrumentFrom
///
/// SELL (XMR→USDT): claimedDepositAmount=1 XMR → amountToGet = USDT per 1 XMR = sell price
/// BUY  (USDT→XMR): claimedDepositAmount=probe USDT → amountToGet = XMR received → buyPrice = probe / amountToGet
/// </summary>
public sealed class SwapgateClient : ISwapgateClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly SwapgateOptions opt;

    public string ExchangeKey => "swapgate";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public SwapgateClient(HttpClient http, IOptions<SwapgateOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── Instrument cache + resolution ───────────────────────────────────────────
    private readonly record struct Instrument(string Cur, string Net, string Type);
    private readonly SemaphoreSlim _instrLock = new(1, 1);
    private List<Instrument>? _instruments;
    private DateTime _instrumentsAt = DateTime.MinValue;
    private static readonly TimeSpan InstrumentTtl = TimeSpan.FromHours(4);

    private async Task<List<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        if (_instruments is not null && DateTime.UtcNow - _instrumentsAt < InstrumentTtl) return _instruments;
        await _instrLock.WaitAsync(ct);
        try
        {
            if (_instruments is not null && DateTime.UtcNow - _instrumentsAt < InstrumentTtl) return _instruments;

            var (body, status) = await SendAsync("api/v1/instruments/public", ct);
            var list = new List<Instrument>();
            if (body is not null && status is >= 200 and < 300)
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var cur = GetStr(el, "currencyTitle");
                            if (string.IsNullOrWhiteSpace(cur)) continue;
                            list.Add(new Instrument(cur.Trim(), (GetStr(el, "networkTitle") ?? "").Trim(),
                                (GetStr(el, "instrumentType") ?? "").Trim()));
                        }
                }
                catch { /* keep empty */ }
            }
            if (list.Count > 0) { _instruments = list; _instrumentsAt = DateTime.UtcNow; }
            return _instruments ?? list;
        }
        finally { _instrLock.Release(); }
    }

    // Map an AssetRef (ticker + optional network) to Swapgate's (currencyTitle, networkTitle).
    private static (string Currency, string Network)? Resolve(List<Instrument> instruments, AssetRef a)
    {
        var ticker = (a.Ticker ?? "").Trim().ToUpperInvariant();
        if (ticker.Length == 0) return null;

        var matches = instruments.Where(i =>
            i.Cur.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            i.Type.Equals("crypto", StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
            return (ticker, string.IsNullOrWhiteSpace(a.Network) ? ticker : a.Network!.Trim());

        if (!string.IsNullOrWhiteSpace(a.Network))
        {
            var want = NormNet(a.Network!);
            var m = matches.FirstOrDefault(i => NormNet(i.Net) == want);
            if (!m.Equals(default(Instrument))) return (m.Cur, m.Net);
        }

        // No network (or no match): prefer the native chain (network == ticker),
        // then ERC20, then TRC20, then whatever's first.
        var pref = matches.OrderBy(i =>
            i.Net.Equals(i.Cur, StringComparison.OrdinalIgnoreCase) ? 0 :
            NormNet(i.Net) == "erc20" ? 1 :
            NormNet(i.Net) == "trc20" ? 2 : 3).First();
        return (pref.Cur, pref.Net);
    }

    private static string NormNet(string n) => n.Trim().ToLowerInvariant() switch
    {
        "tron" or "trx" or "trc20" => "trc20",
        "ethereum" or "eth" or "erc20" => "erc20",
        "binance smart chain" or "bsc" or "bep20" => "bep20",
        "solana" or "sol" => "sol",
        var x => x,
    };

    // ── SELL: Base → Quote (XMR → USDT/BTC/ETH) ──────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var instruments = await GetInstrumentsAsync(ct);
        var baseI = Resolve(instruments, query.Base);
        var quoteI = Resolve(instruments, query.Quote);
        if (baseI is null || quoteI is null) return null;

        var probe = opt.SellProbeAmountXmr;  // probe is in the base (XMR) currency
        var (amountToGet, minRequired) = await GetRateWithMinAsync(
            baseI.Value.Currency, baseI.Value.Network, quoteI.Value.Currency, quoteI.Value.Network, probe, query.Fixed, ct);

        if (amountToGet is null && minRequired is > 0m)
        {
            probe = minRequired.Value * 1.1m;
            (amountToGet, _) = await GetRateWithMinAsync(
                baseI.Value.Currency, baseI.Value.Network, quoteI.Value.Currency, quoteI.Value.Network, probe, query.Fixed, ct);
        }

        if (amountToGet is null || amountToGet <= 0m) return null;

        var sellPrice = amountToGet.Value / probe;  // quote received per 1 base
        return sellPrice <= 0m ? null : MakeResult(query, sellPrice);
    }

    // ── BUY: Quote → Base (USDT/BTC/ETH → XMR) ───────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var instruments = await GetInstrumentsAsync(ct);
        var baseI = Resolve(instruments, query.Base);
        var quoteI = Resolve(instruments, query.Quote);
        if (baseI is null || quoteI is null) return null;

        // Probe is in the QUOTE currency (PriceService sets it per quote: 0.01 BTC, 0.3 ETH; default USDT).
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;
        var (amountToGet, minRequired) = await GetRateWithMinAsync(
            quoteI.Value.Currency, quoteI.Value.Network, baseI.Value.Currency, baseI.Value.Network, probe, false, ct);

        if (amountToGet is null && minRequired is > 0m)
        {
            probe = minRequired.Value * 1.1m;
            (amountToGet, _) = await GetRateWithMinAsync(
                quoteI.Value.Currency, quoteI.Value.Network, baseI.Value.Currency, baseI.Value.Network, probe, false, ct);
        }

        if (amountToGet is null || amountToGet <= 0m) return null;

        // amountToGet = base received for `probe` of quote → quote per 1 base.
        var buyPrice = probe / amountToGet.Value;
        return buyPrice <= 0m ? null : MakeResult(query, buyPrice);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    // GET /api/v1/instruments/public → [{ currencyTitle, networkTitle, slug,
    // instrumentType ("crypto"|...), ... }]. Keep only crypto (drops fiat like BGN).
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var instruments = await GetInstrumentsAsync(ct);
        return instruments
            .Where(i => i.Type.Equals("crypto", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.Cur))
            .Select(i => new ExchangeCurrency(
                ExchangeId: $"{i.Cur}|{i.Net}".ToLowerInvariant(),
                Ticker: i.Cur.Trim().ToUpperInvariant(),
                Network: i.Net.Trim()))
            .GroupBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ticker)
            .ThenBy(x => x.Network)
            .ToList();
    }

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ── Core rate call ────────────────────────────────────────────────────────
    // Returns (amountToGet, minRequired).
    // If probe is below minimum, amountToGet=null and minRequired is populated
    // from the 422 body so the caller can retry with a larger amount.

    private async Task<(decimal? amountToGet, decimal? minRequired)> GetRateWithMinAsync(
        string fromCurrency, string fromNetwork,
        string toCurrency, string toNetwork,
        decimal depositAmount, bool fixedRate, CancellationToken ct)
    {
        var rateMode = fixedRate ? "FIXED" : "FLOATING"; // FLOATING is the default, unchanged
        var url = $"api/v1/rates/public/one" +
                  $"?instrumentFromCurrencyTitle={Uri.EscapeDataString(fromCurrency)}" +
                  $"&instrumentFromNetworkTitle={Uri.EscapeDataString(fromNetwork)}" +
                  $"&instrumentToCurrencyTitle={Uri.EscapeDataString(toCurrency)}" +
                  $"&instrumentToNetworkTitle={Uri.EscapeDataString(toNetwork)}" +
                  $"&rateMode={rateMode}" +
                  $"&claimedDepositAmount={depositAmount.ToString(CultureInfo.InvariantCulture)}" +
                  $"&markup=0";

        var (body, statusCode) = await SendAsync(url, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (statusCode == 422)
            {
                decimal? min = null;
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("details", out var details) &&
                    details.TryGetProperty("expected", out var expEl))
                {
                    var v = ReadDecimal(expEl);
                    if (v > 0m) min = v;
                }
                Console.WriteLine($"[SWAPGATE] 422 min={min} for {fromCurrency}→{toCurrency}");
                return (null, min);
            }

            if (root.TryGetProperty("amountToGet", out var el))
            {
                var v = ReadDecimal(el);
                if (v > 0m) return (v, null);
            }
            if (root.TryGetProperty("price", out var pel))
            {
                var v = ReadDecimal(pel);
                if (v > 0m) return (v, null);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<(string? body, int statusCode)> SendAsync(string relativeUrl, CancellationToken ct)
    {
        var baseUrl = opt.BaseUrl.TrimEnd('/');
        var fullUrl = $"{baseUrl}/{relativeUrl.TrimStart('/')}";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));

        Console.WriteLine($"[SWAPGATE] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                req.Headers.TryAddWithoutValidation("User-Agent", opt.UserAgent);

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[SWAPGATE] Timed out: {fullUrl}");
                return (null, 0);
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);
            var code = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode && code != 422)
            {
                Console.WriteLine($"[SWAPGATE] HTTP {code}: {body}");
                return (null, code);
            }

            return (body, code);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SWAPGATE] Error: {fullUrl} — {ex.Message}");
            return (null, 0);
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