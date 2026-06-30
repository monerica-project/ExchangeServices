using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// BitXChange API client.
/// Docs: https://api.bitxchange.io
///
/// Auth: X-API-Key header.
///
/// Generalized over any Base/Quote pair (XMR↔USDT/BTC/ETH …). Each AssetRef is
/// resolved to BitXChange's (symbol, network); a bare ticker uses its native chain.
///
/// SELL (Base→Quote): from=Base amount=probe → to_amount = quote received; price = to_amount/probe
/// BUY  (Quote→Base): from=Quote amount=probe → to_amount = base received;  price = probe/to_amount
/// </summary>
public sealed class BitXChangeClient : IBitXChangeClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly CultureInfo InvCulture = CultureInfo.InvariantCulture;

    private readonly HttpClient _http;
    private readonly BitXChangeOptions opt;

    public string ExchangeKey => "bitxchange";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public BitXChangeClient(HttpClient http, IOptions<BitXChangeOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // ── Asset resolution ───────────────────────────────────────────────────────
    // Map an AssetRef (ticker + optional network) to BitXChange's (symbol, network).
    // A bare ticker resolves to its native chain (XMR→XMR, BTC→BTC, ETH→ETH,
    // USDT→TRC20 via opts); an explicit AssetRef.Network is honoured as-is.
    private (string Symbol, string Network)? Resolve(AssetRef a)
    {
        var ticker = (a.Ticker ?? "").Trim().ToUpperInvariant();
        if (ticker.Length == 0) return null;
        var network = string.IsNullOrWhiteSpace(a.Network) ? NativeNetwork(ticker) : a.Network!.Trim();
        return (ticker, network);
    }

    private string NativeNetwork(string ticker)
    {
        if (ticker.Equals(opt.XmrSymbol, StringComparison.OrdinalIgnoreCase)) return opt.XmrNetwork;
        if (ticker.Equals(opt.UsdtSymbol, StringComparison.OrdinalIgnoreCase)) return opt.UsdtNetwork;
        // For native-chain assets BitXChange's network string equals the ticker
        // (verified: BTC→BTC, ETH→ETH).
        return ticker;
    }

    // ── SELL: Base → Quote (XMR → USDT/BTC/ETH) ──────────────────────────────
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var b = Resolve(query.Base);
        var q = Resolve(query.Quote);
        if (b is null || q is null) return null;

        var probe = 1m; // probe in the base currency; amount=1 keeps XMR→USDT behaviour identical
        var (toAmount, minDeposit) = await GetRateAsync(
            b.Value.Symbol, b.Value.Network, q.Value.Symbol, q.Value.Network, probe, ct);

        if (toAmount is null && minDeposit is > 0m)
        {
            probe = minDeposit.Value * 1.1m;
            (toAmount, _) = await GetRateAsync(
                b.Value.Symbol, b.Value.Network, q.Value.Symbol, q.Value.Network, probe, ct);
        }

        if (toAmount is null || toAmount <= 0m) return null;
        var sellPrice = toAmount.Value / probe; // quote received per 1 base
        return sellPrice <= 0m ? null : MakeResult(query, sellPrice);
    }

    // ── BUY: Quote → Base (USDT/BTC/ETH → XMR) ───────────────────────────────
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var b = Resolve(query.Base);
        var q = Resolve(query.Quote);
        if (b is null || q is null) return null;

        // Probe is denominated in the QUOTE currency (PriceService sets it per quote:
        // 0.01 BTC, 0.3 ETH; default = opt.BuyProbeAmountUsdt for USDT).
        var probe = query.ProbeAmount ?? opt.BuyProbeAmountUsdt;
        var (toAmount, minDeposit) = await GetRateAsync(
            q.Value.Symbol, q.Value.Network, b.Value.Symbol, b.Value.Network, probe, ct);

        if (toAmount is null && minDeposit is > 0m)
        {
            probe = minDeposit.Value * 1.1m;
            (toAmount, _) = await GetRateAsync(
                q.Value.Symbol, q.Value.Network, b.Value.Symbol, b.Value.Network, probe, ct);
        }

        if (toAmount is null || toAmount <= 0m) return null;
        // toAmount = base received for `probe` of quote → quote spent per 1 base.
        var buyPrice = probe / toAmount.Value;
        return buyPrice <= 0m ? null : MakeResult(query, buyPrice);
    }

    // ── Currencies ────────────────────────────────────────────────────────────
    // BitXChange's proxy (api.bitxchange.io) exposes no single "list all coins"
    // endpoint, but two endpoints together cover the full universe (discovered from
    // the site's /api-docs: GET /crypto, /crypto/limits, /network, /price, /order):
    //
    //   GET /crypto/limits      → [{ "name": "<TICKER>", "min_deposit", "max_deposit" }, …]
    //                             the authoritative list of every supported ticker.
    //   GET /crypto?coin=TICKER → [{ "short_name": "USDT", "is_active": true,
    //                               "deposit_networks":  [{ "name": "TRC20", "is_active": true }, …],
    //                               "withdraw_networks": [{ "name": "ERC20", … }, …],
    //                               "default_network":   { "name": "ERC20", … } }]
    //                             per-coin networks (`coin` is an exact short_name filter;
    //                             there is no bulk/"all" variant — bare /crypto 500s).
    //
    // The `short_name` (ticker) and network `name` are exactly the from/to and
    // from_network/to_network values the /price endpoint consumes (e.g. USDT+TRC20,
    // XMR+XMR). We enumerate the limits list, then fan out (bounded) one /crypto call
    // per ticker, and emit one ExchangeCurrency per active network.
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var baseUrl = opt.BaseUrl.TrimEnd('/');

        // 1. Full ticker universe.
        var limitsBody = await GetAsync($"{baseUrl}/crypto/limits", ct);
        if (string.IsNullOrEmpty(limitsBody)) return Array.Empty<ExchangeCurrency>();

        var tickers = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(limitsBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    {
                        var s = n.GetString()?.Trim();
                        if (!string.IsNullOrEmpty(s)) tickers.Add(s);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITXCHANGE] limits parse error: {ex.Message}");
            return Array.Empty<ExchangeCurrency>();
        }

        tickers = tickers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (tickers.Count == 0) return Array.Empty<ExchangeCurrency>();

        // 2. Per-ticker networks (bounded concurrency).
        var bag = new ConcurrentBag<ExchangeCurrency>();
        using var gate = new SemaphoreSlim(Math.Clamp(opt.CurrencyFetchConcurrency, 1, 32));

        var tasks = tickers.Select(async ticker =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var body = await GetAsync($"{baseUrl}/crypto?coin={Uri.EscapeDataString(ticker)}", ct);
                if (string.IsNullOrEmpty(body)) return;
                AddCoinNetworks(body, bag);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BITXCHANGE] crypto parse error for {ticker}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        return bag
            .GroupBy(c => c.ExchangeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(c => c.Ticker, StringComparer.Ordinal)
            .ThenBy(c => c.Network, StringComparer.Ordinal)
            .ToList();
    }

    // Parse a /crypto?coin=… response (array of coin objects) and add one
    // ExchangeCurrency per active deposit/withdraw network of each active coin.
    private static void AddCoinNetworks(string body, ConcurrentBag<ExchangeCurrency> bag)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        foreach (var coin in doc.RootElement.EnumerateArray())
        {
            if (!coin.TryGetProperty("short_name", out var snEl) || snEl.ValueKind != JsonValueKind.String)
                continue;
            var ticker = snEl.GetString()?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(ticker)) continue;

            // Skip wholly inactive coins.
            if (coin.TryGetProperty("is_active", out var act) && act.ValueKind == JsonValueKind.False)
                continue;

            var networks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectActiveNetworks(coin, "deposit_networks", networks);
            CollectActiveNetworks(coin, "withdraw_networks", networks);

            // Fall back to the default network if the lists were empty.
            if (networks.Count == 0 &&
                coin.TryGetProperty("default_network", out var dn) && dn.ValueKind == JsonValueKind.Object &&
                dn.TryGetProperty("name", out var dnn) && dnn.ValueKind == JsonValueKind.String)
            {
                var d = dnn.GetString()?.Trim();
                if (!string.IsNullOrEmpty(d)) networks.Add(d);
            }

            foreach (var net in networks)
                bag.Add(new ExchangeCurrency(
                    ExchangeId: $"{ticker}|{net}",
                    Ticker: ticker,
                    Network: net));
        }
    }

    private static void CollectActiveNetworks(JsonElement coin, string prop, HashSet<string> into)
    {
        if (!coin.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var net in arr.EnumerateArray())
        {
            if (net.ValueKind != JsonValueKind.Object) continue;
            if (net.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.False) continue;
            if (net.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
            {
                var s = nm.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s)) into.Add(s);
            }
        }
    }

    // ── Core rate call ──────────────────────────────────────────────────────────
    // Sends `amount` of `from` and returns (to_amount received, min_deposit).
    // If `amount` is below the minimum the API returns to_amount=null and the
    // min_deposit (in the `from` currency) so the caller can retry with more.
    private async Task<(decimal? toAmount, decimal? minDeposit)> GetRateAsync(
        string from, string fromNetwork,
        string to, string toNetwork,
        decimal amount, CancellationToken ct)
    {
        var qs = $"from={Uri.EscapeDataString(from)}" +
                 $"&to={Uri.EscapeDataString(to)}" +
                 $"&type=variable" +
                 $"&amount={amount.ToString(InvCulture)}";

        if (!string.IsNullOrWhiteSpace(fromNetwork))
            qs += $"&from_network={Uri.EscapeDataString(fromNetwork)}";
        if (!string.IsNullOrWhiteSpace(toNetwork))
            qs += $"&to_network={Uri.EscapeDataString(toNetwork)}";

        var fullUrl = $"{opt.BaseUrl.TrimEnd('/')}/price?{qs}";
        var body = await GetAsync(fullUrl, ct);
        if (body is null) return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // For amount-based requests the API returns to_amount = units of `to`
            // received (the "price" field mirrors to_amount when amount is supplied).
            var toAmount = root.TryGetProperty("to_amount", out var taEl) ? ReadDecimal(taEl) : 0m;
            if (toAmount <= 0m && root.TryGetProperty("price", out var pEl))
                toAmount = ReadDecimal(pEl);
            var minDeposit = root.TryGetProperty("min_deposit", out var minEl) ? ReadDecimal(minEl) : 0m;

            if (toAmount <= 0m)
            {
                Console.WriteLine($"[BITXCHANGE] zero rate for {from}→{to} amount={amount}: {body}");
                return (null, minDeposit > 0m ? minDeposit : null);
            }

            if (minDeposit > 0m && amount < minDeposit)
                return (null, minDeposit);

            return (toAmount, minDeposit > 0m ? minDeposit : null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITXCHANGE] parse error: {ex.Message} — {body}");
            return (null, null);
        }
    }

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private async Task<string?> GetAsync(string fullUrl, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 30));
        Console.WriteLine($"[BITXCHANGE] GET {fullUrl}");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            req.Headers.TryAddWithoutValidation("X-API-Key", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            var sendTask = _http.SendAsync(req, ct);
            var timeoutTask = Task.Delay(timeout, ct);

            if (await Task.WhenAny(sendTask, timeoutTask) == timeoutTask)
            {
                Console.WriteLine($"[BITXCHANGE] Timed out");
                return null;
            }

            using var resp = await sendTask;
            var body = await resp.Content.ReadAsStringAsync(CancellationToken.None);

            Console.WriteLine($"[BITXCHANGE] HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}");

            if (!resp.IsSuccessStatusCode) return null;
            return body;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BITXCHANGE] Error: {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static decimal ReadDecimal(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var d)) return d;
        if (el.ValueKind == JsonValueKind.String &&
            decimal.TryParse(el.GetString(), NumberStyles.Any, InvCulture, out var ds)) return ds;
        return 0m;
    }

    private PriceResult MakeResult(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);
}