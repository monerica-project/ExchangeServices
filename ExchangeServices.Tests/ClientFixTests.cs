using System.Net;
using ExchangeServices.Abstractions;
using ExchangeServices.Implementations;
using ExchangeServices.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExchangeServices.Tests;

public class ClientFixTests
{
    private static readonly AssetRef Xmr = new("XMR");
    private static readonly AssetRef Btc = new("BTC");
    private static readonly AssetRef Usdt = new("USDT", "Tron");

    // ── SimpleSwap: buy probe sized in the QUOTE currency + (1 - fee) correction ────────────
    [Fact]
    public async Task SimpleSwap_Buy_SendsCallerProbe_AndDividesByIt()
    {
        var handler = new FakeHttpHandler((req, _, _) =>
            req.RequestUri!.AbsolutePath.Contains("currencies")
                ? FakeHttpHandler.Json("""[{"symbol":"xmr","network":"xmr"},{"symbol":"btc","network":"btc"}]""")
                : FakeHttpHandler.Json("""{"result":{"estimatedAmount":0.5}}"""));
        var client = new SimpleSwapClient(FakeHttpHandler.Client(handler), Options.Create(new SimpleSwapOptions { ApiKey = "k" }));

        var res = await client.GetBuyPriceAsync(new PriceQuery(Xmr, Btc, ProbeAmount: 0.025m));

        Assert.NotNull(res);
        var estimate = handler.Requests.Single(r => r.Url.Contains("estimates"));
        Assert.Contains("tickerFrom=btc", estimate.Url);
        Assert.Contains("amount=0.025", estimate.Url);
        Assert.Equal(0.025m / 0.5m * 0.996m, res!.Price);
        Assert.Equal(0.996m, SimpleSwapClient.ApiRateCorrection);
    }

    // ── ChangeNow: a float query never falls back to the fixed-rate flow ───────────────────
    [Theory]
    [InlineData("standard")]
    [InlineData("fixed-rate")]
    [InlineData(null)]
    public void ChangeNow_FloatFlows_NeverIncludeFixedRate(string? configured)
    {
        var flows = ChangeNowClient.BuildFlowAttempts(configured);
        Assert.DoesNotContain("fixed-rate", flows, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("standard", flows);
    }

    // ── FixedFloat: rate type follows the query (no float→fixed fallback; buy honours Fixed) ─
    private static FixedFloatClient FixedFloat(FakeHttpHandler h) => new(
        FakeHttpHandler.Client(h),
        Options.Create(new FixedFloatOptions { ApiKey = "k", ApiSecret = "s", SiteName = "FF", SiteUrl = "x" }),
        NullLogger<FixedFloatClient>.Instance);

    private static HttpResponseMessage FfNoQuote() =>
        FakeHttpHandler.Json("""{"code":0,"msg":"","data":{"errors":["LIMIT_MAX"]}}""");

    [Fact]
    public async Task FixedFloat_FloatSell_OnlyRequestsFloat()
    {
        var h = new FakeHttpHandler((_, _, _) => FfNoQuote());
        var res = await FixedFloat(h).GetSellPriceAsync(new PriceQuery(Xmr, Btc, 1m, Fixed: false));

        Assert.Null(res);
        Assert.NotEmpty(h.Requests);
        Assert.All(h.Requests, r => Assert.DoesNotContain("\"fixed\"", r.Body));
    }

    [Fact]
    public async Task FixedFloat_FixedBuy_RequestsFixed()
    {
        var h = new FakeHttpHandler((_, _, _) => FfNoQuote());
        await FixedFloat(h).GetBuyPriceAsync(new PriceQuery(Xmr, Btc, 0.02m, Fixed: true));

        Assert.NotEmpty(h.Requests);
        Assert.All(h.Requests, r => Assert.Contains("\"fixed\"", r.Body));
    }

    // ── LetsExchange: a retried POST gets fresh content (old code reused a disposed StringContent)
    [Fact]
    public async Task LetsExchange_RetriedPost_DoesNotReuseDisposedContent()
    {
        var h = new FakeHttpHandler((_, _, n) => n == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : FakeHttpHandler.Json("""{"min_amount":"0.01","max_amount":"100","amount":"350"}"""));
        var client = new LetsExchangeClient(FakeHttpHandler.Client(h),
            Options.Create(new LetsExchangeOptions { ApiKey = "k", AffiliateId = "a", RetryCount = 1 }));

        var res = await client.GetSellPriceAsync(new PriceQuery(Xmr, Usdt, ProbeAmount: 1m));

        Assert.Equal(2, h.Requests.Count);
        Assert.Equal(h.Requests[0].Body, h.Requests[1].Body);
        Assert.Equal(350m, res!.Price);
    }

    // ── Exolix: only USDT walks the network ladder; memo is per pair ───────────────────────
    [Fact]
    public async Task Exolix_NonUsdtPair_SendsNoNetworks()
    {
        var h = new FakeHttpHandler((_, _, _) => FakeHttpHandler.Json("""{"fromAmount":1,"toAmount":0.004}"""));
        var client = new ExolixClient(FakeHttpHandler.Client(h, "https://exolix.test/api/v2/"),
            Options.Create(new ExolixOptions { SiteName = "Exolix", SiteUrl = "x" }));

        var res = await client.GetSellPriceAsync(new PriceQuery(Xmr, Btc, 1m));

        Assert.Equal(0.004m, res!.Price);
        var url = Assert.Single(h.Requests).Url;
        Assert.DoesNotContain("network", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Exolix_UsdtPair_LaddersAndMemoisesPerPair()
    {
        // Only SOL works for USDT.
        var h = new FakeHttpHandler((req, _, _) => req.RequestUri!.Query.Contains("=SOL")
            ? FakeHttpHandler.Json("""{"fromAmount":1,"toAmount":350}""")
            : new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        var client = new ExolixClient(FakeHttpHandler.Client(h, "https://exolix.test/api/v2/"),
            Options.Create(new ExolixOptions { SiteName = "Exolix", SiteUrl = "x", RetryCount = 0 }));

        var first = await client.GetSellPriceAsync(new PriceQuery(Xmr, Usdt, 1m));
        Assert.Equal(350m, first!.Price);
        Assert.Contains("networkTo=ETH", h.Requests[0].Url);   // ladder starts at ETH
        Assert.DoesNotContain("networkFrom", h.Requests[0].Url); // XMR side unqualified

        h.Requests.Clear();
        await client.GetSellPriceAsync(new PriceQuery(Xmr, Usdt, 1m));
        Assert.Contains("networkTo=SOL", h.Requests[0].Url);    // memoised for this pair

        h.Requests.Clear();
        await client.GetSellPriceAsync(new PriceQuery(Xmr, Btc, 1m));
        Assert.DoesNotContain("SOL", h.Requests[0].Url);        // not leaked into XMR->BTC
    }

    // ── Trocador: the amount on the wire is the amount we divide by; tiny amounts aren't "0" ─
    [Theory]
    [InlineData(7.123456, "7.1235")]
    [InlineData(0.025, "0.025")]
    [InlineData(0.00002, "0.00002")]
    [InlineData(2500, "2500")]
    public void Trocador_FormatsRoundedAmount(double amount, string expected)
        => Assert.Equal(expected, TrocadorClient.FormatAmount((decimal)amount));

    [Fact]
    public void Trocador_RoundAmount_MatchesWhatIsSent()
    {
        Assert.Equal(7.1235m, TrocadorClient.RoundAmount(7.123456m));
        Assert.Equal(0.00002m, TrocadorClient.RoundAmount(0.00002m));
    }

    // ── BitXChange: friendly network names map to BitXChange's own network names ──────────
    [Theory]
    [InlineData("USDT", "Tron", "TRC20")]
    [InlineData("USDT", null, "TRC20")]
    [InlineData("USDT", "Ethereum", "ERC20")]
    [InlineData("XMR", null, "XMR")]
    [InlineData("BTC", "Mainnet", "BTC")]
    [InlineData("ETH", "Ethereum", "ETH")]
    public void BitXChange_MapsNetworkNames(string ticker, string? network, string expected)
    {
        var client = new BitXChangeClient(new HttpClient(), Options.Create(new BitXChangeOptions()));
        Assert.Equal(expected, client.MapNetwork(ticker, network));
    }

    // ── Changee: an empty API key must not throw while masking the logged URL ──────────────
    [Fact]
    public async Task Changee_EmptyApiKey_DoesNotThrow()
    {
        var h = new FakeHttpHandler((_, _, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new ChangeeClient(FakeHttpHandler.Client(h), Options.Create(new ChangeeOptions { ApiKey = "" }));

        var res = await client.GetSellPriceAsync(new PriceQuery(Xmr, Usdt, 1m));
        Assert.Null(res);
    }

    // ── DI: SocketsHttpHandler primaries with an infinite pooled lifetime get recycled ──────
    [Fact]
    public void ConnectionRecycling_SetsPooledLifetime_OnlyWhenInfinite()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("infinite").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
        services.AddHttpClient("explicit").ConfigurePrimaryHttpMessageHandler(() =>
            new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(1) });
        services.AddExchangeConnectionRecycling();

        using var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpMessageHandlerFactory>();

        Assert.Equal(ServiceCollectionExtensions.ExchangePooledConnectionLifetime,
            Primary(factory.CreateHandler("infinite")).PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(1), Primary(factory.CreateHandler("explicit")).PooledConnectionLifetime);
    }

    [Fact]
    public void ConnectionRecycling_DefaultPrimaryHandler_IsBounded()
    {
        // Clients that don't configure a primary handler get the framework default; make sure
        // that is not a pinned (infinite-lifetime) connection pool either.
        var services = new ServiceCollection();
        services.AddHttpClient("default");
        services.AddExchangeConnectionRecycling();

        using var sp = services.BuildServiceProvider();
        var primary = Unwrap(sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("default"));

        var s = Assert.IsType<SocketsHttpHandler>(primary);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, s.PooledConnectionLifetime);
    }

    private static HttpMessageHandler Unwrap(HttpMessageHandler h)
    {
        while (h is DelegatingHandler d && d.InnerHandler is not null) h = d.InnerHandler;
        return h;
    }

    private static SocketsHttpHandler Primary(HttpMessageHandler h) => Assert.IsType<SocketsHttpHandler>(Unwrap(h));
}
