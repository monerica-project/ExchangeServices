using ExchangeServices.Abstractions;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;
using ExchangeServices.Http;

namespace ExchangeServices.SageSwap;

public sealed class SageSwapClient : ISageSwapClient, IExchangeCurrencyApi
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // SageSwap quotes XMR<->USDT on ERC20 and SOL (NOT Tron). USDT is ~$1 on every
    // chain, so for a price feed any of these is a valid USDT/XMR figure. Preference
    // order; the board's USDT(TRC20) isn't offered for XMR so we fall back to ERC20.
    private static readonly string[] UsdtPref =
        { "USDTERC20", "USDTSOL", "USDTBSC", "USDTMATIC", "USDTARBITRUM", "USDTTRC20" };

    private readonly HttpClient http;
    private readonly SageSwapOptions opt;
    private readonly string ratesXmlUrl;

    // Short-lived cache so a sell+buy in the same refresh cycle = one feed fetch.
    private static readonly TimeSpan FeedTtl = TimeSpan.FromSeconds(12);
    private readonly SemaphoreSlim feedLock = new(1, 1);
    private List<RateItem>? feedItems;
    private DateTimeOffset feedAt;

    public string  ExchangeKey => "sageswap";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public char PrivacyLevel => opt.PrivacyLevel;
    public decimal MinAmountUsd => opt.MinAmountUsd;

    public SageSwapClient(HttpClient http, IOptions<SageSwapOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
        // currencies.xml lives at the site root, not under /api — derive from the host.
        this.ratesXmlUrl = new Uri(new Uri(opt.BaseUrl), "/currencies.xml").ToString();
    }

    private sealed record RateItem(string From, string To, decimal In, decimal Out);

    // SELL: 1 XMR -> USDT.  Feed row from=XMR, to=USDT* ; price = out/in (USDT per XMR).
    // SELL: base -> quote. Feed row from=base, to=quote ; price = out/in (quote per base).
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var items = await GetFeedAsync(ct);
        var baseT = (query.Base.Ticker ?? "XMR").Trim().ToUpperInvariant();
        var quoteT = (query.Quote.Ticker ?? "USDT").Trim().ToUpperInvariant();
        var row = PickRow(items, baseT, quoteT, fromBase: true);
        if (row is null || row.In <= 0 || row.Out <= 0) return null;

        var price = row.Out / row.In; // quote received per 1 base sold
        if (price <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, price, DateTimeOffset.UtcNow);
    }

    // BUY: quote -> base. Feed row from=quote, to=base ; price = in/out (quote per base).
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var items = await GetFeedAsync(ct);
        var baseT = (query.Base.Ticker ?? "XMR").Trim().ToUpperInvariant();
        var quoteT = (query.Quote.Ticker ?? "USDT").Trim().ToUpperInvariant();
        var row = PickRow(items, baseT, quoteT, fromBase: false);
        if (row is null || row.In <= 0 || row.Out <= 0) return null;

        var price = row.In / row.Out; // quote paid per 1 base bought
        if (price <= 0) return null;

        return new PriceResult(ExchangeKey, query.Base, query.Quote, price, DateTimeOffset.UtcNow);
    }

    // Pick the base<->quote row. For USDT the feed uses chain-suffixed variants
    // (USDTERC20, USDTSOL, …) so we try those in preference; for BTC/ETH/etc. the
    // ticker is matched exactly.
    private static RateItem? PickRow(IReadOnlyList<RateItem> items, string baseTicker, string quoteTicker, bool fromBase)
    {
        bool IsBase(string s) => s.Equals(baseTicker, StringComparison.OrdinalIgnoreCase);

        var quoteCandidates = quoteTicker.Equals("USDT", StringComparison.OrdinalIgnoreCase)
            ? UsdtPref
            : new[] { quoteTicker };

        foreach (var q in quoteCandidates)
        {
            var hit = items.FirstOrDefault(i => fromBase
                ? IsBase(i.From) && i.To.Equals(q, StringComparison.OrdinalIgnoreCase)
                : IsBase(i.To) && i.From.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        // USDT: accept any chain variant not in the preference list.
        if (quoteTicker.Equals("USDT", StringComparison.OrdinalIgnoreCase))
        {
            return items.FirstOrDefault(i => fromBase
                ? IsBase(i.From) && i.To.StartsWith("USDT", StringComparison.OrdinalIgnoreCase)
                : IsBase(i.To) && i.From.StartsWith("USDT", StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    // =========================
    // RATES FEED (BestChange-standard currencies.xml), cached briefly.
    // =========================
    private async Task<IReadOnlyList<RateItem>> GetFeedAsync(CancellationToken ct)
    {
        if (feedItems is not null && DateTimeOffset.UtcNow - feedAt < FeedTtl)
            return feedItems;

        await feedLock.WaitAsync(ct);
        try
        {
            if (feedItems is not null && DateTimeOffset.UtcNow - feedAt < FeedTtl)
                return feedItems;

            using var req = new HttpRequestMessage(HttpMethod.Get, ratesXmlUrl);
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            if (!string.IsNullOrWhiteSpace(opt.UserAgent))
                req.Headers.UserAgent.ParseAdd(opt.UserAgent);

            var res = await http.SendForStringWithTimeoutAsync(req, DefaultTimeout, ct);
            if (res is null ||
                res.StatusCode < HttpStatusCode.OK ||
                res.StatusCode >= HttpStatusCode.MultipleChoices)
            {
                if (res is not null)
                    Console.WriteLine($"[SAGESWAP] feed {(int)res.StatusCode} from {ratesXmlUrl}");
                return feedItems ?? (IReadOnlyList<RateItem>)Array.Empty<RateItem>();
            }

            var parsed = ParseFeed(res.Body);
            if (parsed.Count > 0)
            {
                feedItems = parsed;
                feedAt = DateTimeOffset.UtcNow;
            }
            return feedItems ?? (IReadOnlyList<RateItem>)Array.Empty<RateItem>();
        }
        catch (OperationCanceledException)
        {
            return feedItems ?? (IReadOnlyList<RateItem>)Array.Empty<RateItem>();
        }
        finally { feedLock.Release(); }
    }

    private static List<RateItem> ParseFeed(string xml)
    {
        var list = new List<RateItem>();
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var item in doc.Descendants("item"))
            {
                var from = (string?)item.Element("from");
                var to   = (string?)item.Element("to");
                if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) continue;

                if (!decimal.TryParse((string?)item.Element("in"),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var inn)) continue;
                if (!decimal.TryParse((string?)item.Element("out"),
                        NumberStyles.Any, CultureInfo.InvariantCulture, out var outt)) continue;

                list.Add(new RateItem(from.Trim(), to.Trim(), inn, outt));
            }
        }
        catch { /* return whatever parsed */ }
        return list;
    }

    // =========================
    // CURRENCIES — still from the JSON API (unchanged) for IExchangeCurrencyApi.
    // =========================
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "api/v1/currencies");
        AddHeaders(req);

        var res = await http.SendForStringWithTimeoutAsync(req, DefaultTimeout, ct);
        if (res is null) return Array.Empty<ExchangeCurrency>();
        if (res.StatusCode < HttpStatusCode.OK || res.StatusCode >= HttpStatusCode.MultipleChoices)
            return Array.Empty<ExchangeCurrency>();

        CurrenciesResponse? dto;
        try { dto = JsonSerializer.Deserialize<CurrenciesResponse>(res.Body, JsonOpt); }
        catch { return Array.Empty<ExchangeCurrency>(); }

        if (dto?.Data is null || dto.Data.Count == 0)
            return Array.Empty<ExchangeCurrency>();

        return dto.Data
            .Select(x => new ExchangeCurrency(
                ExchangeId: x.FriendlyId,
                Ticker: x.Ticker,
                Network: x.Network?.Name ?? ""))
            .ToList();
    }

    private void AddHeaders(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(opt.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.Token);

        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── DTOs (currencies) ─────────────────────────────────────────────────
    private sealed class CurrenciesResponse
    {
        public List<CurrencyDto> Data { get; set; } = new();
    }

    private sealed class CurrencyDto
    {
        public string FriendlyId { get; set; } = "";
        public string Ticker { get; set; } = "";
        public NetworkDto? Network { get; set; }
    }

    private sealed class NetworkDto
    {
        public string Name { get; set; } = "";
    }
}