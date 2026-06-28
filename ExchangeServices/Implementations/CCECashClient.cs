using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

public sealed class CCECashClient : ICCECashClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    // Fee multipliers derived from CCE's actual exchange widget vs reference price:
    //   Sell: 1 XMR -> USDT, CCE takes ~0.9% so user receives less
    //   Buy:  USDT -> 1 XMR, CCE takes ~0.92% so user pays more
    private const decimal SellFeeMultiplier = 0.991m;
    private const decimal BuyFeeMultiplier = 1.0092m;

    private readonly HttpClient _http;
    private readonly CCECashOptions opt;

    public string ExchangeKey => "ccecash";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public CCECashClient(HttpClient http, IOptions<CCECashOptions> options)
    {
        _http = http;
        opt = options.Value;
    }

    // =========================
    // SELL: 1 XMR -> USDT price
    // =========================
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        // The recent-prices feed is USDT-denominated, so it's only valid when the
        // quote IS USDT. For BTC/ETH (etc.) fall through to /calculate, which prices
        // the actual requested pair.
        var quoteIsUsdt = (query.Quote.Ticker ?? "").Trim().ToUpperInvariant() == "USDT";
        var ticker = (query.Base.Ticker ?? "").Trim().ToUpperInvariant();
        var price = quoteIsUsdt ? await GetRecentPriceForAsync(ticker, ct) : 0m;

        if (price > 0)
        {
            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: price * SellFeeMultiplier,
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: null,
                Raw: null
            );
        }

        // BTC/ETH or no recent price: calculate honors the actual quote.
        return await GetPriceViaCalculateAsync(query, ct);
    }

    // ==========================================
    // BUY: USDT needed to receive 1 XMR
    // ==========================================
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        // USDT-only fast path (recent prices are in USDT); BTC/ETH go via /calculate.
        var quoteIsUsdt = (query.Quote.Ticker ?? "").Trim().ToUpperInvariant() == "USDT";
        var ticker = (query.Base.Ticker ?? "").Trim().ToUpperInvariant();
        var price = quoteIsUsdt ? await GetRecentPriceForAsync(ticker, ct) : 0m;

        if (price > 0)
        {
            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: price * BuyFeeMultiplier,
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: null,
                Raw: null
            );
        }

        // BTC/ETH or no recent price: calculate honors the actual quote.
        return await GetBuyPriceViaCalculateAsync(query, ct);
    }

    // =========================
    // CURRENCIES
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/abbr/lists");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        AddSignatureHeaders(req, queryString: "", bodyString: "");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[CCE CURRENCIES] Status={res?.Status}, Body={res?.Body?[..Math.Min(300, res?.Body?.Length ?? 0)]}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
            return Array.Empty<ExchangeCurrency>();

        var dto = JsonSerializer.Deserialize<AbbrListResponse>(res.Body, JsonOpt);
        if (dto is null || dto.Code != 0 || dto.Data is null || dto.Data.Count == 0)
            return Array.Empty<ExchangeCurrency>();

        // data is keyed by chain name; the chain is the key, items have no "chain" field.
        return dto.Data
            .SelectMany(group => (group.Value ?? new List<AbbrItem>())
                .Where(x => !x.Disabled && !string.IsNullOrWhiteSpace(x.Abbr))
                .Select(x =>
                {
                    var abbr = x.Abbr!.Trim().ToUpperInvariant();
                    var chain = group.Key.Trim();
                    return new ExchangeCurrency(
                        ExchangeId: $"{abbr}:{chain}",
                        Ticker: abbr,
                        Network: NormalizeNetworkFromChain(chain)
                    );
                }))
            .GroupBy(x => x.ExchangeId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ticker)
            // Native/home chain first per ticker, so a consumer that takes the first
            // match for a bare ticker (e.g. PriceService) gets the canonical chain
            // (ETH→Ethereum) instead of a deposit-disabled L2 (ETH→Arbitrum).
            .ThenByDescending(x => ChainOfId(x.ExchangeId).Equals(NativeChainFor(x.Ticker), StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Network)
            .ToList();
    }

    private static string ChainOfId(string exchangeId)
    {
        var p = exchangeId.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return p.Length == 2 ? p[1] : "";
    }

    // =========================
    // RECENT PRICES — single call, all tickers vs USDT
    // =========================
    private async Task<decimal> GetRecentPriceForAsync(string ticker, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/abbr/recent/prices");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        AddSignatureHeaders(req, queryString: "", bodyString: "");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[CCE RECENT] Status={res?.Status}, Body={res?.Body?[..Math.Min(300, res?.Body?.Length ?? 0)]}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
            return 0m;

        var dto = JsonSerializer.Deserialize<RecentPricesResponse>(res.Body, JsonOpt);

        Console.WriteLine($"[CCE RECENT] Code={dto?.Code}, Msg={dto?.Msg}, Count={dto?.Data?.Count}");

        if (dto is null || dto.Code != 0 || dto.Data is null)
            return 0m;

        var match = dto.Data.FirstOrDefault(p => p.Abbr.Equals(ticker, StringComparison.OrdinalIgnoreCase));
        return match?.Price ?? 0m;
    }

    // =========================
    // CALCULATE — fallback only, single call
    // =========================
    private async Task<PriceResult?> GetPriceViaCalculateAsync(PriceQuery query, CancellationToken ct)
    {
        var (fromAbbr, fromChain) = await ResolveAbbrChainAsync(query.Base, ct) ?? default;
        var (toAbbr, toChain) = await ResolveAbbrChainAsync(query.Quote, ct) ?? default;

        Console.WriteLine($"[CCE SELL CALC] fromAbbr={fromAbbr}, fromChain={fromChain}, toAbbr={toAbbr}, toChain={toChain}");

        if (string.IsNullOrWhiteSpace(fromAbbr) || string.IsNullOrWhiteSpace(toAbbr)) return null;

        var data = await CalculateAsync(fromAbbr, fromChain, 1m, toAbbr, toChain, ct);
        var outAmt = data?.To?.FirstOrDefault()?.ToQuantity ?? 0m;
        if (outAmt <= 0) return null;

        // Calculate endpoint already returns post-fee amount — no multiplier needed
        return new PriceResult(ExchangeKey, query.Base, query.Quote, outAmt, DateTimeOffset.UtcNow, data?.OrderId, null);
    }

    private async Task<PriceResult?> GetBuyPriceViaCalculateAsync(PriceQuery query, CancellationToken ct)
    {
        var (usdtAbbr, usdtChain) = await ResolveAbbrChainAsync(query.Quote, ct) ?? default;
        var (xmrAbbr, xmrChain) = await ResolveAbbrChainAsync(query.Base, ct) ?? default;

        Console.WriteLine($"[CCE BUY CALC] usdtAbbr={usdtAbbr}, usdtChain={usdtChain}, xmrAbbr={xmrAbbr}, xmrChain={xmrChain}");

        if (string.IsNullOrWhiteSpace(usdtAbbr) || string.IsNullOrWhiteSpace(xmrAbbr)) return null;

        var data = await CalculateAsync(usdtAbbr, usdtChain, 200m, xmrAbbr, xmrChain, ct);
        var xmrOut = data?.To?.FirstOrDefault()?.ToQuantity ?? 0m;

        Console.WriteLine($"[CCE BUY CALC] xmrOut={xmrOut}");

        if (xmrOut <= 0) return null;

        // Calculate endpoint already returns post-fee amount — no multiplier needed
        var price = 200m / xmrOut;
        return new PriceResult(ExchangeKey, query.Base, query.Quote, price, DateTimeOffset.UtcNow, data?.OrderId, null);
    }

    private async Task<CalcData?> CalculateAsync(
        string fromAbbr, string fromChain, decimal fromQty,
        string toAbbr, string toChain,
        CancellationToken ct)
    {
        var reqObj = new CalcRequest
        {
            ExchangeMode = "float",
            FromAbbr = fromAbbr,
            FromChain = fromChain,
            FromQuantity = fromQty,
            ToAddress = new List<ToLegRequest>
            {
                new() { ToAbbr = toAbbr, ToChain = toChain, ToRatio = 1m }
            }
        };

        var json = JsonSerializer.Serialize(reqObj, JsonOpt);
        Console.WriteLine($"[CCE CALC] Request: {json}");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/order/calculate");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Content = SafeHttpExtensions.JsonContent(json);
        AddSignatureHeaders(req, queryString: "", bodyString: json);

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[CCE CALC] Status={res?.Status}, Body={res?.Body}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        var dto = JsonSerializer.Deserialize<CalcResponse>(res.Body, JsonOpt);

        Console.WriteLine($"[CCE CALC] Code={dto?.Code}, Msg={dto?.Msg}");

        if (dto is null || dto.Code != 0 || dto.Data is null) return null;

        return dto.Data;
    }

    // =========================
    // RESOLUTION
    // =========================
    private async Task<(string Abbr, string Chain)?> ResolveAbbrChainAsync(AssetRef asset, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(asset.ExchangeId))
        {
            var parts = asset.ExchangeId.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2) return (parts[0].ToUpperInvariant(), parts[1]);
        }

        var t = (asset.Ticker ?? "").Trim().ToUpperInvariant();

        if (t.StartsWith("USDT") && (t.Contains("TRC") || (asset.Network?.Equals("Tron", StringComparison.OrdinalIgnoreCase) ?? false)))
            return ("USDT", "TRON");

        if (t == "XMR") return ("XMR", "Monero");

        var chainGuess = GuessChainFromNetworkOrTicker(asset);
        if (!string.IsNullOrWhiteSpace(chainGuess))
            return (t, chainGuess);

        var all = await GetCurrenciesAsync(ct);
        if (all.Count == 0) return null;

        // ExchangeId is "ABBR:Chain" (chain = CCE's original name, e.g. "Ethereum").
        static string ChainOf(ExchangeCurrency c)
        {
            var p = c.ExchangeId.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return p.Length == 2 ? p[1] : "";
        }

        var wantNet = (asset.Network ?? "").Trim();
        // When no network is given, prefer the coin's native/home chain — otherwise
        // the first match can be a deposit-disabled L2 (e.g. ETH on Arbitrum).
        var native = NativeChainFor(t);

        var match = (!string.IsNullOrWhiteSpace(wantNet)
                        ? all.FirstOrDefault(x => x.Ticker.Equals(t, StringComparison.OrdinalIgnoreCase) &&
                                                  x.Network.Equals(wantNet, StringComparison.OrdinalIgnoreCase))
                        : null)
                    ?? (native is not null
                        ? all.FirstOrDefault(x => x.Ticker.Equals(t, StringComparison.OrdinalIgnoreCase) &&
                                                  ChainOf(x).Equals(native, StringComparison.OrdinalIgnoreCase))
                        : null)
                    ?? all.FirstOrDefault(x => x.Ticker.Equals(t, StringComparison.OrdinalIgnoreCase));

        if (match is null) return null;

        var p = match.ExchangeId.Split(':', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return p.Length == 2 ? (p[0].ToUpperInvariant(), p[1]) : null;
    }

    // CCE's native/home chain name for common coins, used to avoid picking a
    // deposit-disabled L2 variant when no explicit network is requested.
    private static string? NativeChainFor(string ticker) => ticker.ToUpperInvariant() switch
    {
        "ETH" => "Ethereum",
        "BTC" => "Bitcoin",
        "BNB" => "BNB Smart Chain",
        "SOL" => "Solana",
        "TRX" => "Tron",
        "LTC" => "Litecoin",
        "BCH" => "Bitcoin Cash",
        "DOGE" => "Dogecoin",
        "ADA" => "Cardano",
        _ => null,
    };

    private static string? GuessChainFromNetworkOrTicker(AssetRef asset)
    {
        var net = (asset.Network ?? "").Trim();
        var t = (asset.Ticker ?? "").Trim().ToUpperInvariant();

        if (net.Equals("Tron", StringComparison.OrdinalIgnoreCase)) return "TRON";
        if (net.Equals("Ethereum", StringComparison.OrdinalIgnoreCase)) return "Ethereum";
        if (net.Equals("Binance Smart Chain", StringComparison.OrdinalIgnoreCase)) return "BSC";
        if (net.Equals("Solana", StringComparison.OrdinalIgnoreCase)) return "Solana";

        if (net.Equals("Mainnet", StringComparison.OrdinalIgnoreCase))
        {
            return t switch
            {
                "XMR" => "Monero",
                "BTC" => "Bitcoin",
                "LTC" => "Litecoin",
                "DOGE" => "Dogecoin",
                "DASH" => "Dash",
                _ => null
            };
        }

        return null;
    }

    private static string NormalizeNetworkFromChain(string chain)
    {
        return chain.Trim().ToLowerInvariant() switch
        {
            "tron" or "trx" => "Tron",
            "ethereum" or "eth" => "Ethereum",
            "bsc" or "binance smart chain" => "Binance Smart Chain",
            "solana" or "sol" => "Solana",
            "monero" or "bitcoin"
                or "litecoin" or "dogecoin"
                or "dash" => "Mainnet",
            var c => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c)
        };
    }

    // =========================
    // SIGNATURE
    // =========================
    private void AddSignatureHeaders(HttpRequestMessage req, string queryString, string bodyString)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey) || string.IsNullOrWhiteSpace(opt.ApiSecret))
        {
            Console.WriteLine("[CCE SIG] WARNING: ApiKey or ApiSecret is empty — requests will be unauthenticated");
            return;
        }

        var nonce = Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var payload = (opt.ApiKey ?? "") + nonce + ts + (queryString ?? "") + (bodyString ?? "");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(opt.ApiSecret!));
        var sign = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        req.Headers.TryAddWithoutValidation("X-Api-Key", opt.ApiKey);
        req.Headers.TryAddWithoutValidation("X-Api-Nonce", nonce);
        req.Headers.TryAddWithoutValidation("X-Api-Timestamp", ts);
        req.Headers.TryAddWithoutValidation("X-Api-Signature", sign);

        Console.WriteLine($"[CCE SIG] key={opt.ApiKey[..Math.Min(6, opt.ApiKey.Length)]}***, ts={ts}, sign={sign[..8]}***");
    }

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

    // =========================
    // DTOs
    // =========================
    private sealed class RecentPricesResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("data")] public List<RecentPriceItem>? Data { get; set; }
    }

    private sealed class RecentPriceItem
    {
        [JsonPropertyName("abbr")] public string Abbr { get; set; } = "";
        [JsonPropertyName("price")] public decimal Price { get; set; }
        [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
        [JsonPropertyName("stable_coin")] public bool StableCoin { get; set; }
    }

    private sealed class AbbrListResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        // The live API groups coins by chain name: { "Monero": [...], "Tron": [...] }.
        // The chain is the dictionary key; items carry no "chain" field.
        [JsonPropertyName("data")] public Dictionary<string, List<AbbrItem>>? Data { get; set; }
    }

    private sealed class AbbrItem
    {
        [JsonPropertyName("abbr")] public string? Abbr { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("decimal")] public int Decimal { get; set; }
        [JsonPropertyName("disabled")] public bool Disabled { get; set; }
        [JsonPropertyName("disabled_send")] public bool DisabledSend { get; set; }
        [JsonPropertyName("disabled_recv")] public bool DisabledRecv { get; set; }
        [JsonPropertyName("contract")] public string? Contract { get; set; }
    }

    private sealed class CalcRequest
    {
        [JsonPropertyName("exchange_mode")] public string ExchangeMode { get; set; } = "float";
        [JsonPropertyName("from_abbr")] public string FromAbbr { get; set; } = "";
        [JsonPropertyName("from_chain")] public string FromChain { get; set; } = "";
        [JsonPropertyName("from_quantity")] public decimal FromQuantity { get; set; }
        [JsonPropertyName("to_address")] public List<ToLegRequest> ToAddress { get; set; } = new();
    }

    private sealed class ToLegRequest
    {
        [JsonPropertyName("to_abbr")] public string ToAbbr { get; set; } = "";
        [JsonPropertyName("to_chain")] public string ToChain { get; set; } = "";
        [JsonPropertyName("to_ratio")] public decimal ToRatio { get; set; } = 1m;
    }

    private sealed class CalcResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("data")] public CalcData? Data { get; set; }
    }

    private sealed class CalcData
    {
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("from_abbr")] public string? FromAbbr { get; set; }
        [JsonPropertyName("from_chain")] public string? FromChain { get; set; }
        [JsonPropertyName("from_quantity")] public decimal FromQuantity { get; set; }
        [JsonPropertyName("min_from_quantity")] public decimal MinFromQuantity { get; set; }
        [JsonPropertyName("max_from_quantity")] public decimal MaxFromQuantity { get; set; }
        [JsonPropertyName("fee")] public decimal Fee { get; set; }
        [JsonPropertyName("to")] public List<ToLeg>? To { get; set; }
    }

    private sealed class ToLeg
    {
        [JsonPropertyName("to_abbr")] public string? ToAbbr { get; set; }
        [JsonPropertyName("to_chain")] public string? ToChain { get; set; }
        [JsonPropertyName("to_ratio")] public decimal ToRatio { get; set; }
        [JsonPropertyName("to_quantity")] public decimal ToQuantity { get; set; }
        [JsonPropertyName("to_address")] public string? ToAddress { get; set; }
    }
}
