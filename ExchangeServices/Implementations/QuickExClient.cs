using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// Quickex.io API v1 client.
///
/// No API key required — the rate endpoint is public. Quickex's WAF blocks
/// dotnet/httpclient User-Agent strings (returns 403), so we send a browser UA.
/// This (not any auth/api-key) was the real reason this client previously "failed".
///
/// Rate endpoint:
///   GET /api/v1/rates/public/one
///     ?exchangeType=crypto
///     &instrumentFromCurrencyTitle=XMR &instrumentFromNetworkTitle=XMR
///     &instrumentToCurrencyTitle=BTC   &instrumentToNetworkTitle=BTC
///     &claimedDepositAmount=1 &claimedDepositAmountCurrency=XMR
///     &rateMode=FLOATING &markup=0
///
///   response.amountToGet = units of instrumentTo received for claimedDepositAmount of instrumentFrom.
///
/// SELL (Base→Quote): probe Base → amountToGet = Quote → sellPrice = amountToGet / probe
/// BUY  (Quote→Base): probe Quote → amountToGet = Base  → buyPrice  = probe / amountToGet
/// 422 → parse data.details.expected for minimum, retry at 110%.
///
/// Currency/network "titles" are resolved from /api/v1/instruments/public, preferring
/// the native chain for a bare ticker. Works two-way for XMR↔USDT, XMR↔BTC, XMR↔ETH.
/// </summary>
public sealed class QuickexClient : IQuickexClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Fallback browser UA if the option is somehow blank.
    private const string DefaultBrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/120 Safari/537.36";

    private readonly HttpClient _http;
    private readonly QuickexOptions opt;

    public string ExchangeKey => "quickex";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public QuickexClient(HttpClient http, IOptions<QuickexOptions> options)
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

            var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/api/v1/instruments/public";
            var (body, code) = await GetAsync(fullUrl, ct);
            var list = new List<Instrument>();
            if (body is not null && code is >= 200 and < 300)
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

    // Map an AssetRef (ticker + optional network) to Quickex's (currencyTitle, networkTitle).
    private (string Currency, string Network)? Resolve(List<Instrument> instruments, AssetRef a)
    {
        var ticker = (a.Ticker ?? "").Trim().ToUpperInvariant();
        if (ticker.Length == 0) return null;

        var matches = instruments.Where(i =>
            i.Cur.Equals(ticker, StringComparison.OrdinalIgnoreCase) &&
            i.Type.Equals("crypto", StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            // No instruments list (or unknown ticker): fall back to configured defaults
            // for the known assets so XMR/USDT keep working even if the list call fails.
            if (ticker == "XMR") return (opt.XmrCurrency, opt.XmrNetwork);
            if (ticker == "USDT") return (opt.UsdtCurrency, opt.UsdtNetwork);
            return (ticker, string.IsNullOrWhiteSpace(a.Network) ? ticker : a.Network!.Trim());
        }

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

        var (amt, min) = await FetchRateAsync(
            baseI.Value.Currency, baseI.Value.Network,
            quoteI.Value.Currency, quoteI.Value.Network,
            probe, baseI.Value.Currency, ct, isFixed: query.Fixed);

        if (amt is null && min is > 0m)
        {
            probe = min.Value * 1.1m;
            (amt, _) = await FetchRateAsync(
                baseI.Value.Currency, baseI.Value.Network,
                quoteI.Value.Currency, quoteI.Value.Network,
                probe, baseI.Value.Currency, ct, isFixed: query.Fixed);
        }

        if (amt is null || amt <= 0m) return null;

        var sellPrice = amt.Value / probe;  // quote received per 1 base
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

        var (amt, min) = await FetchRateAsync(
            quoteI.Value.Currency, quoteI.Value.Network,
            baseI.Value.Currency, baseI.Value.Network,
            probe, quoteI.Value.Currency, ct);

        if (amt is null && min is > 0m)
        {
            probe = min.Value * 1.1m;
            (amt, _) = await FetchRateAsync(
                quoteI.Value.Currency, quoteI.Value.Network,
                baseI.Value.Currency, baseI.Value.Network,
                probe, quoteI.Value.Currency, ct);
        }

        if (amt is null || amt <= 0m) return null;

        // amt = base received for `probe` of quote → quote spent per 1 base.
        var buyPrice = probe / amt.Value;
        return buyPrice <= 0m ? null : MakeResult(query, buyPrice);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    // GET /api/v1/instruments/public → [{ currencyTitle, networkTitle, instrumentType, ... }].
    // Keep only crypto (drops fiat like RON).
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

    // ── Core fetch ────────────────────────────────────────────────────────────

    private async Task<(decimal? amountToGet, decimal? minRequired)> FetchRateAsync(
        string fromCcy, string fromNet,
        string toCcy, string toNet,
        decimal depositAmount, string depositCcy,
        CancellationToken ct,
        bool isFixed = false)
    {
        var qs = $"exchangeType=crypto" +
                 $"&instrumentFromCurrencyTitle={Uri.EscapeDataString(fromCcy)}" +
                 $"&instrumentFromNetworkTitle={Uri.EscapeDataString(fromNet)}" +
                 $"&instrumentToCurrencyTitle={Uri.EscapeDataString(toCcy)}" +
                 $"&instrumentToNetworkTitle={Uri.EscapeDataString(toNet)}" +
                 $"&claimedDepositAmount={depositAmount.ToString(CultureInfo.InvariantCulture)}" +
                 $"&claimedDepositAmountCurrency={Uri.EscapeDataString(depositCcy)}" +
                 $"&rateMode={(isFixed ? "FIXED" : "FLOATING")}" +
                 $"&markup=0";

        if (!string.IsNullOrWhiteSpace(opt.ReferrerId))
            qs += $"&referrerId={Uri.EscapeDataString(opt.ReferrerId)}";

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/api/v1/rates/public/one?{qs}";

        var (body, code) = await GetAsync(fullUrl, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (code == 422)
            {
                decimal? min = null;
                if (root.TryGetProperty("data", out var d) &&
                    d.TryGetProperty("details", out var det) &&
                    det.TryGetProperty("expected", out var expEl))
                {
                    var v = ParseDecimal(expEl);
                    if (v > 0m) min = v;
                }
                Console.WriteLine($"[QUICKEX] 422 min={min} ({fromCcy}→{toCcy})");
                return (null, min);
            }

            foreach (var field in new[] { "amountToGet", "price" })
                if (root.TryGetProperty(field, out var el))
                {
                    var v = ParseDecimal(el);
                    if (v > 0m) return (v, null);
                }

            Console.WriteLine($"[QUICKEX] No rate field in: {body}");
            return (null, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QUICKEX] Parse error: {ex.Message}");
            return (null, null);
        }
    }

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ── HTTP ──────────────────────────────────────────────────────────────────

    private async Task<(string? body, int code)> GetAsync(string fullUrl, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        var ua = string.IsNullOrWhiteSpace(opt.UserAgent) ? DefaultBrowserUserAgent : opt.UserAgent;

        Console.WriteLine($"[QUICKEX] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);

            // Must look like a browser or Quickex WAF returns 403. No API key needed.
            req.Headers.TryAddWithoutValidation("User-Agent", ua);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            req.Headers.TryAddWithoutValidation("Referer", "https://quickex.io/");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[QUICKEX] Timed out");
                return (null, 0);
            }

            using var resp = await sendTask;
            var code = (int)resp.StatusCode;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            if (!resp.IsSuccessStatusCode && code != 422)
            {
                Console.WriteLine($"[QUICKEX] HTTP {code}: {body}");
                return (null, code);
            }

            return (body, code);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[QUICKEX] Error: {ex.Message}");
            return (null, 0);
        }
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
