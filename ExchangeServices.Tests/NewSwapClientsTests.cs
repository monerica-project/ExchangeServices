using ExchangeServices.Abstractions;
using ExchangeServices.Implementations;
using Microsoft.Extensions.Options;
using Xunit;

namespace ExchangeServices.Tests;

/// <summary>Mistex, IronSwaps, BetKarma, PlataSwap and Nanswap: price math, limits and coin codes.</summary>
public class NewSwapClientsTests
{
    private static readonly AssetRef Xmr = new("XMR");
    private static readonly AssetRef Btc = new("BTC");
    private static readonly AssetRef UsdtTron = new("USDT", "Tron");

    [Fact]
    public async Task Mistex_sell_is_rate_and_buy_is_inverse()
    {
        var h = new FakeHttpHandler((_, _, _) => FakeHttpHandler.Json("""{"rate":0.0075,"min_amount":0.1,"max_amount":1000}"""));
        var c = new MistexClient(FakeHttpHandler.Client(h), Options.Create(new MistexOptions()));

        var sell = await c.GetSellPriceAsync(new PriceQuery(Xmr, Btc, 4m));
        Assert.Equal(0.0075m, sell!.Price);
        Assert.Contains("coin_from=XMR&coin_to=BTC&network_from=XMR&network_to=BTC&amount=4", h.Requests[0].Url);

        h = new FakeHttpHandler((_, _, _) => FakeHttpHandler.Json("""{"rate":0.0016,"min_amount":10,"max_amount":100000}"""));
        c = new MistexClient(FakeHttpHandler.Client(h), Options.Create(new MistexOptions()));
        var buy = await c.GetBuyPriceAsync(new PriceQuery(Xmr, UsdtTron, 2500m));
        Assert.Equal(625m, buy!.Price);
        Assert.Contains("coin_from=USDT&coin_to=XMR&network_from=TRC20", h.Requests[0].Url);
    }

    [Fact]
    public async Task Mistex_above_max_is_unavailable()
    {
        var h = new FakeHttpHandler((_, _, _) => FakeHttpHandler.Json("""{"rate":0.0075,"min_amount":0.1,"max_amount":2}"""));
        var c = new MistexClient(FakeHttpHandler.Client(h), Options.Create(new MistexOptions()));
        Assert.Null(await c.GetSellPriceAsync(new PriceQuery(Xmr, Btc, 4m)));
    }

    [Fact]
    public async Task IronSwaps_uses_received_over_sent()
    {
        var h = new FakeHttpHandler((_, _, _) => FakeHttpHandler.Json(
            """{"quote":{"amountFrom":4,"amountTo":0.031,"amountUserReceives":0.03,"min":0,"max":0}}"""));
        var c = new IronSwapsClient(FakeHttpHandler.Client(h), Options.Create(new IronSwapsOptions()));

        var sell = await c.GetSellPriceAsync(new PriceQuery(Xmr, Btc, 4m));
        Assert.Equal(0.0075m, sell!.Price);
        Assert.Contains("\"from\":\"xmr_mainnet\"", h.Requests[0].Body);
        Assert.Contains("\"to\":\"btc_mainnet\"", h.Requests[0].Body);
        Assert.Equal("usdt_trx", IronSwapsClient.Code(UsdtTron));
        Assert.Equal("usdt_eth", IronSwapsClient.Code(new AssetRef("USDT", "Ethereum")));
    }

    [Fact]
    public async Task BetKarma_buy_inverts_out_per_unit_and_checks_limits()
    {
        var h = new FakeHttpHandler((_, _, _) => FakeHttpHandler.Json(
            """{"ok":true,"estimate":{"outPerUnit":"130","minAmount":"0.0025","maxAmount":"15"}}"""));
        var c = new BetKarmaClient(FakeHttpHandler.Client(h), Options.Create(new BetKarmaOptions()));

        var buy = await c.GetBuyPriceAsync(new PriceQuery(Xmr, Btc, 0.026m));
        Assert.Equal(1m / 130m, buy!.Price);
        Assert.EndsWith("estimate?pair=btc/xmr", h.Requests[0].Url);

        Assert.Null(await c.GetBuyPriceAsync(new PriceQuery(Xmr, Btc, 20m))); // above max
        Assert.Equal("usdt_tron", BetKarmaClient.Code(UsdtTron));
    }

    [Fact]
    public async Task PlataSwap_not_ok_is_unavailable()
    {
        var h = new FakeHttpHandler((_, _, n) => FakeHttpHandler.Json(n == 1
            ? """{"ok":true,"receive":"0.03"}"""
            : """{"ok":false,"error":"amount_above_max"}"""));
        var c = new PlataSwapClient(FakeHttpHandler.Client(h), Options.Create(new PlataSwapOptions()));

        Assert.Equal(0.0075m, (await c.GetSellPriceAsync(new PriceQuery(Xmr, Btc, 4m)))!.Price);
        Assert.Null(await c.GetSellPriceAsync(new PriceQuery(Xmr, Btc, 4000m)));
        Assert.Equal("usdttrc20", PlataSwapClient.Code(UsdtTron));
    }

    [Fact]
    public async Task Nanswap_buy_uses_board_probe_and_tron_usdt()
    {
        var h = new FakeHttpHandler((_, _, _) => FakeHttpHandler.Json("""{"amountFrom":2500,"amountTo":4}"""));
        var c = new NanswapClient(FakeHttpHandler.Client(h), Options.Create(new ExchangeServices.Models.NanswapOptions()));

        var buy = await c.GetBuyPriceAsync(new PriceQuery(Xmr, UsdtTron, 2500m));
        Assert.Equal(625m, buy!.Price);
        Assert.Single(h.Requests);
        Assert.Contains("from=USDT-TRX&to=XMR&amount=2500", h.Requests[0].Url);
    }
}
