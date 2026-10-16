using ExchangeServices.Abstractions;
using Xunit;

namespace ExchangeServices.Tests;

public class ProbeSizingTests
{
    private static PriceQuery Q(string quote, decimal? probe)
        => new(new AssetRef("XMR"), new AssetRef(quote), probe);

    [Fact]
    public void BuyProbe_UsesCallerProbe_WhenPositive()
        => Assert.Equal(0.025m, ProbeSizing.BuyProbe(Q("BTC", 0.025m), 500m));

    [Fact]
    public void BuyProbe_RoundsCallerProbeTo8dp()
        => Assert.Equal(0.02512346m, ProbeSizing.BuyProbe(Q("BTC", 0.025123456789m), 500m));

    [Theory]
    [InlineData("USDT", 500)]
    [InlineData("usdc", 500)]
    [InlineData("BTC", 0.01)]
    [InlineData("ETH", 0.3)]
    [InlineData("LTC", 1)]
    public void BuyProbe_FallsBackPerQuoteCurrency_WhenNotSized(string quote, double expected)
    {
        Assert.Equal((decimal)expected, ProbeSizing.BuyProbe(Q(quote, null), 500m));
        Assert.Equal((decimal)expected, ProbeSizing.BuyProbe(Q(quote, 0m), 500m));
        Assert.Equal((decimal)expected, ProbeSizing.BuyProbe(Q(quote, -3m), 500m));
    }

    [Fact]
    public void Round_NeverReturnsZeroForPositiveInput()
    {
        Assert.Equal(0.00000001m, ProbeSizing.Round(0.000000001m));
        Assert.Equal(0m, ProbeSizing.Round(0m));
    }

    [Theory]
    [InlineData("USDT", true)]
    [InlineData(" usdc ", true)]
    [InlineData("BTC", false)]
    [InlineData(null, false)]
    public void IsUsdStable(string? ticker, bool expected)
        => Assert.Equal(expected, ProbeSizing.IsUsdStable(ticker));
}
