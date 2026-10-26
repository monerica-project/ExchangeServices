using ExchangeServices.Abstractions;
using ExchangeServices.Implementations;
using Xunit;

namespace ExchangeServices.Tests;

// Swapuz lists wrapped coins (BTC/ETH on Arbitrum, BSC, ...). A quote with no network must use
// the coin's own chain, never the first catalog match (Arbitrum BTC is withdraw-only, which
// made XMR/BTC buys fail and dropped Swapuz from the BTC board).
public class SwapuzNetworkTests
{
    [Theory]
    [InlineData("BTC", "BTC:ARB", "BTC", "BTC")]
    [InlineData("ETH", "ETH:ARB", "ETH", "ETH")]
    [InlineData("BTC", null, "BTC", "BTC")]
    public void NoNetwork_UsesTheCoinsOwnChain(string ticker, string? catalogId, string expectedTicker, string expectedNet)
    {
        var (t, n) = SwapuzClient.ResolveTickerNetwork(new AssetRef(ticker, null, catalogId));
        Assert.Equal((expectedTicker, expectedNet), (t, n));
    }

    [Fact]
    public void XmrAndUsdt_KeepTheirFixedNetworks()
    {
        Assert.Equal(("XMR", "XMR"), SwapuzClient.ResolveTickerNetwork(new AssetRef("XMR")));
        Assert.Equal(("USDT", "TRX"), SwapuzClient.ResolveTickerNetwork(new AssetRef("USDT", "Tron", "USDT:TRX")));
    }

    [Fact]
    public void ExplicitNetwork_UsesTheResolvedCatalogId()
    {
        Assert.Equal(("ETH", "BASE"), SwapuzClient.ResolveTickerNetwork(new AssetRef("ETH", "BASE", "ETH:BASE")));
    }
}
