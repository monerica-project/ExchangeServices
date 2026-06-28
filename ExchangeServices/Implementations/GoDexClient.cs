using ExchangeServices.Abstractions;
using ExchangeServices.Http;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExchangeServices.Implementations;

/// <summary>
/// GoDex API v1 client.
///
/// Endpoints used:
///   GET  /api/v1/coins            → list of supported coins
///   POST /api/v1/info             → floating rate: send N of coin_from, receive ? of coin_to
///
/// Auth: optional "public-key" header for affiliate tracking.
///
/// Sell (1 XMR → USDT):
///   POST /info { from=XMR, to=USDT, amount=1, float=true, network_from=XMR, network_to=TRX }
///   response.amount = USDT received → that is the sell price
///
/// Buy (USDT → 1 XMR):
///   POST /info { from=USDT, to=XMR, amount=probe, float=true, network_from=TRX, network_to=XMR }
///   response.amount = XMR received → buy price = probe / response.amount
///
/// No explicit fee correction applied: GoDex's /info endpoint already returns the
/// post-fee amount the user actually receives, which matches what the site shows.
/// </summary>
public sealed class GoDexClient : IGoDexClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive  = true,
        NumberHandling               = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient   _http;
    private readonly GoDexOptions opt;

    public string  ExchangeKey => "godex";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public GoDexClient(HttpClient http, IOptions<GoDexOptions> options)
    {
        _http = http;
        opt  = options.Value;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SELL: 1 Base → Quote (e.g. 1 XMR → USDT/BTC/ETH)
    //   Ask: "if I send 1 Base, how much Quote do I receive?"
    //   sell price = Quote received for 1 Base.
    // ════════════════════════════════════════════════════════════════════════
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (fromCode, fromNetwork) = Resolve(query.Base);   // sending the base
        var (toCode,   toNetwork)   = Resolve(query.Quote);  // receiving the quote

        var dto      = await PostInfoAsync(fromCode, toCode, fromNetwork, toNetwork, amount: 1m, ct);
        var received = dto?.Amount ?? 0m;
        // GoDex signals unavailable pairs with amount:"0"/rate:"0" → treat as no price.
        if (received <= 0m) return null;

        return new PriceResult(
            Exchange:      ExchangeKey,
            Base:          query.Base,
            Quote:         query.Quote,
            Price:         received,            // already post-fee: quote received for 1 base
            TimestampUtc:  DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw:           $"sell 1 {fromCode}→{toCode} received={received} rate={dto?.Rate}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // BUY: Quote → 1 Base
    //   Ask: "if I send probeAmount of Quote, how much Base do I receive?"
    //   buy price = probeAmount / baseReceived  (quote per 1 base)
    // ════════════════════════════════════════════════════════════════════════
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var (fromCode, fromNetwork) = Resolve(query.Quote);  // spending the quote
        var (toCode,   toNetwork)   = Resolve(query.Base);   // receiving the base (XMR)

        // Probe is denominated in the QUOTE currency; honor PriceService's per-quote
        // probe when set, otherwise fall back to the client's own default.
        var probe    = query.ProbeAmount ?? PickProbeAmount(query.Quote);
        var dto      = await PostInfoAsync(fromCode, toCode, fromNetwork, toNetwork, amount: probe, ct);
        var received = dto?.Amount ?? 0m;
        // GoDex signals unavailable pairs (e.g. ETH→XMR) with amount:"0" → treat as no price.
        if (received <= 0m) return null;

        var buyPrice = probe / received;  // quote per 1 base

        return new PriceResult(
            Exchange:      ExchangeKey,
            Base:          query.Base,
            Quote:         query.Quote,
            Price:         buyPrice,
            TimestampUtc:  DateTimeOffset.UtcNow,
            CorrelationId: null,
            Raw:           $"buy probe={probe} {fromCode}→{toCode} baseOut={received:F6} price={buyPrice:F6} rate={dto?.Rate}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CURRENCIES: GET /api/v1/coins
    // ════════════════════════════════════════════════════════════════════════
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = BuildRequest(HttpMethod.Get, "/api/v1/coins");
        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300)
            return Array.Empty<ExchangeCurrency>();

        var coins = JsonSerializer.Deserialize<List<CoinDto>>(res.Body, JsonOpt);
        if (coins is null) return Array.Empty<ExchangeCurrency>();

        return coins
            .Where(c => !string.IsNullOrWhiteSpace(c.Code) && c.Disabled == 0)
            .Select(c =>
            {
                var ticker = c.Code!.Trim().ToUpperInvariant();
                return new ExchangeCurrency(
                    ExchangeId: ticker,
                    Ticker:     ticker,
                    Network:    GuessNetwork(ticker));
            })
            .OrderBy(c => c.Ticker)
            .ToList();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Private helpers
    // ════════════════════════════════════════════════════════════════════════

    private async Task<InfoDto?> PostInfoAsync(
        string fromCode, string toCode,
        string fromNetwork, string toNetwork,
        decimal amount,
        CancellationToken ct)
    {
        var body = new InfoRequest
        {
            From        = fromCode,
            To          = toCode,
            Amount      = amount,
            Float       = true,
            NetworkFrom = fromNetwork,
            NetworkTo   = toNetwork,
        };

        var json = JsonSerializer.Serialize(body, JsonOpt);

        using var req = BuildRequest(HttpMethod.Post, "/api/v1/info");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var res = await SafeHttpExtensions.SendForStringAsync(_http, req, Timeout(), ct);

        Console.WriteLine($"[GoDex] {fromCode}({fromNetwork})→{toCode}({toNetwork}) amt={amount} " +
                          $"status={res?.Status} body={res?.Body?[..Math.Min(200, res?.Body?.Length ?? 0)]}");

        if (res is null || (int)res.Status < 200 || (int)res.Status >= 300) return null;

        return JsonSerializer.Deserialize<InfoDto>(res.Body, JsonOpt);
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.TryAddWithoutValidation("Accept",       "application/json");
        req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        if (!string.IsNullOrWhiteSpace(opt.ApiKey))
            req.Headers.TryAddWithoutValidation("public-key", opt.ApiKey);
        return req;
    }

    /// <summary>Returns a sensible probe amount for the buy-side query.</summary>
    private static decimal PickProbeAmount(AssetRef quote)
    {
        return (quote.Ticker ?? "").Trim().ToUpperInvariant() switch
        {
            "BTC"  => 0.005m,   // ~$350 at typical XMR/BTC rate
            "ETH"  => 0.2m,
            "LTC"  => 3m,
            _      => 300m,     // stablecoin default
        };
    }

    /// <summary>
    /// Resolves an AssetRef to GoDex's (ticker, network) pair. Tickers and networks
    /// are always UPPERCASE.
    ///
    /// Native chains use the ticker as the network identifier (XMR→(XMR,XMR),
    /// BTC→(BTC,BTC), ETH→(ETH,ETH)). USDT collapses to ticker "USDT" with the chain
    /// carried by the network, honoring an explicit AssetRef.Network when present and
    /// defaulting to TRX (Tron). Note GoDex uses "ETH" — not "ERC20" — for the
    /// Ethereum-chain USDT network.
    /// </summary>
    private static (string Ticker, string Network) Resolve(AssetRef asset)
    {
        var ticker = (asset.Ticker ?? "").Trim().ToUpperInvariant();

        if (ticker.StartsWith("USDT"))
        {
            var net = (asset.Network ?? "").Trim().ToLowerInvariant();
            var network = net switch
            {
                ""                                        => "TRX",   // default USDT to Tron
                "tron" or "trx" or "trc20"                => "TRX",
                "ethereum" or "eth" or "erc20"            => "ETH",
                "binance smart chain" or "bsc" or "bep20" => "BSC",
                "solana" or "sol"                         => "SOL",
                _                                         => net.ToUpperInvariant(),
            };
            return ("USDT", network);
        }

        // Native chains: GoDex uses the coin code as the network identifier.
        var nativeNetwork = ticker switch
        {
            "BNB" => "BSC",
            _     => ticker,   // XMR→XMR, BTC→BTC, ETH→ETH, LTC→LTC, SOL→SOL, DOGE→DOGE, …
        };
        return (ticker, nativeNetwork);
    }

    /// <summary>Best-guess canonical network name from a GoDex coin code.</summary>
    private static string GuessNetwork(string ticker) => ticker switch
    {
        "XMR"  => "Mainnet",
        "BTC"  => "Mainnet",
        "LTC"  => "Mainnet",
        "ETH"  => "Ethereum",
        "BNB"  => "Binance Smart Chain",
        "SOL"  => "Solana",
        "USDT" => "Tron",       // default USDT to TRC20 in currency list
        _      => "Mainnet",
    };

    private TimeSpan Timeout() =>
        TimeSpan.FromSeconds(Math.Clamp(opt.RequestTimeoutSeconds, 2, 60));

    // ════════════════════════════════════════════════════════════════════════
    // DTOs
    // ════════════════════════════════════════════════════════════════════════

    private sealed class InfoRequest
    {
        [JsonPropertyName("from")]         public string  From        { get; set; } = "";
        [JsonPropertyName("to")]           public string  To          { get; set; } = "";
        [JsonPropertyName("amount")]       public decimal Amount      { get; set; }
        [JsonPropertyName("float")]        public bool    Float       { get; set; } = true;
        [JsonPropertyName("network_from")] public string  NetworkFrom { get; set; } = "";
        [JsonPropertyName("network_to")]   public string  NetworkTo   { get; set; } = "";
    }

    private sealed class InfoDto
    {
        [JsonPropertyName("amount")]     public decimal Amount    { get; set; }  // what user receives
        [JsonPropertyName("rate")]       public decimal Rate      { get; set; }  // exchange rate
        [JsonPropertyName("min_amount")] public decimal MinAmount { get; set; }
        [JsonPropertyName("max_amount")] public decimal MaxAmount { get; set; }
        [JsonPropertyName("fee")]        public decimal Fee       { get; set; }
    }

    private sealed class CoinDto
    {
        [JsonPropertyName("code")]     public string? Code     { get; set; }
        [JsonPropertyName("name")]     public string? Name     { get; set; }
        [JsonPropertyName("disabled")] public int     Disabled { get; set; }
    }
}
