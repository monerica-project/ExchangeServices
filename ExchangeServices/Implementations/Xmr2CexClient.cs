using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// xmr2cex API client. Docs: https://xmr2cex.com/docs
/// Auth: x-api-key: &lt;16-digit account PIN&gt;.
/// Quote: GET /api/v1/quote?amount=&lt;XMR 1..5000&gt;&amp;outputAsset=&lt;asset&gt;
///   → { quoteId, inputAmount, outputAsset, outputAmount, exchangeRate, expiresAt }
///
/// SELL-ONLY: the deposit side is always XMR; the output is one of a fixed set of CEX assets.
/// GetSellPriceAsync therefore returns null unless the query's Base is XMR and the Quote maps to
/// a supported output asset. Per-XMR price = outputAmount / amount.
/// </summary>
public sealed class Xmr2CexClient : IXmr2CexClient
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true,

        // xmr2cex returns numeric fields as JSON strings (e.g. "outputAmount":"1055.39509300"),
        // so allow decimals/longs to be read from strings or the deserialize throws.
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient http;
    private readonly Xmr2CexOptions opt;

    public string ExchangeKey => "xmr2cex";
    public string SiteName => opt.SiteName;
    public string? SiteUrl => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public string RateType => RateTypes.Float;

    public Xmr2CexClient(HttpClient http, IOptions<Xmr2CexOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // Map a requested quote asset (ticker + optional network) to xmr2cex's outputAsset id.
    // Supported: btc, eth, sol, ltc, trx, usdt[/-erc20/-bep20/-arb], usdc-erc20/-bep20/-arb, bnb, hype.
    private static string? MapOutputAsset(AssetRef quote)
    {
        var t = (quote.Ticker ?? string.Empty).Trim().ToLowerInvariant();
        var n = (quote.Network ?? string.Empty).Trim().ToLowerInvariant();

        bool isTron = n is "trc20" or "trx" or "tron" or "";
        bool isErc20 = n is "erc20" or "eth" or "ethereum";
        bool isBep20 = n is "bep20" or "bsc" or "bnb" or "binance" or "binance-smart-chain";
        bool isArb = n is "arb" or "arbitrum" or "arbitrum-one" or "arb1";

        return t switch
        {
            "btc" => "btc",
            "eth" => "eth",
            "sol" => "sol",
            "ltc" => "ltc",
            "trx" => "trx",
            "bnb" => "bnb",
            "hype" => "hype",
            "usdt" when isErc20 => "usdt-erc20",
            "usdt" when isBep20 => "usdt-bep20",
            "usdt" when isArb => "usdt-arb",
            "usdt" when isTron => "usdt",       // xmr2cex's plain "usdt" is TRC20
            "usdc" when isBep20 => "usdc-bep20",
            "usdc" when isArb => "usdc-arb",
            "usdc" when isErc20 => "usdc-erc20",
            "usdc" => "usdc-erc20",              // default to ERC-20 (no plain "usdc")
            _ => null,
        };
    }

    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            return null;
        }

        // Sell-only: the deposit asset must be XMR.
        if (!string.Equals((query.Base.Ticker ?? string.Empty).Trim(), "xmr", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var outputAsset = MapOutputAsset(query.Quote);
        if (outputAsset is null)
        {
            return null;
        }

        // amount is the XMR to send; the API accepts 1..5000.
        var amount = query.ProbeAmount is decimal p && p > 0 ? p : 1m;
        if (amount < 1m)
        {
            amount = 1m;
        }

        if (amount > 5000m)
        {
            amount = 5000m;
        }

        var url = $"/api/v1/quote?amount={amount.ToString(CultureInfo.InvariantCulture)}" +
                  $"&outputAsset={Uri.EscapeDataString(outputAsset)}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-api-key", opt.ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await this.http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                ExchangeLog.Debug($"[XMR2CEX] HTTP {(int)resp.StatusCode} xmr->{outputAsset} amount={amount}: {Trunc(body)}");
                return null;
            }

            var quote = JsonSerializer.Deserialize<QuoteResponse>(body, JsonOpt);
            if (quote is null || quote.OutputAmount <= 0m)
            {
                ExchangeLog.Debug($"[XMR2CEX] no usable outputAmount xmr->{outputAsset}: {Trunc(body)}");
                return null;
            }

            // outputAmount is the asset received for `amount` XMR → per-XMR price = received / sent.
            var price = quote.OutputAmount / amount;

            return new PriceResult(
                Exchange: ExchangeKey,
                Base: query.Base,
                Quote: query.Quote,
                Price: price,
                TimestampUtc: DateTimeOffset.UtcNow,
                CorrelationId: quote.QuoteId,
                Raw: $"xmr->{outputAsset} amount={amount} out={quote.OutputAmount} price={price}",
                MinAmountUsd: opt.MinAmountUsd);
        }
        catch (Exception ex)
        {
            ExchangeLog.Debug($"[XMR2CEX] error xmr->{outputAsset}: {ex.Message}");
            return null;
        }
    }

    // xmr2cex exposes no currencies endpoint; its supported set is fixed (XMR in, these assets out).
    public Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ExchangeCurrency> list = new List<ExchangeCurrency>
        {
            new("xmr", "XMR", "xmr"),
            new("btc", "BTC", "btc"),
            new("eth", "ETH", "eth"),
            new("sol", "SOL", "sol"),
            new("ltc", "LTC", "ltc"),
            new("trx", "TRX", "tron"),
            new("bnb", "BNB", "bsc"),
            new("hype", "HYPE", "hype"),
            new("usdt", "USDT", "trc20"),
            new("usdt-erc20", "USDT", "erc20"),
            new("usdt-bep20", "USDT", "bep20"),
            new("usdt-arb", "USDT", "arbitrum"),
            new("usdc-erc20", "USDC", "erc20"),
            new("usdc-bep20", "USDC", "bep20"),
            new("usdc-arb", "USDC", "arbitrum"),
        };
        return Task.FromResult(list);
    }

    private static string Trunc(string s) => s.Length > 200 ? s[..200] : s;

    private sealed class QuoteResponse
    {
        public string? QuoteId { get; set; }

        public decimal InputAmount { get; set; }

        public string? OutputAsset { get; set; }

        public decimal OutputAmount { get; set; }

        public decimal ExchangeRate { get; set; }

        public long ExpiresAt { get; set; }
    }
}
