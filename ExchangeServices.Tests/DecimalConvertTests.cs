using Xunit;

namespace ExchangeServices.Tests;

public class DecimalConvertTests
{
    [Theory]
    [InlineData(0.3, "0.3")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.01, "0.01")]
    [InlineData(0.00001, "0.00001")]
    [InlineData(123.456, "123.456")]
    [InlineData(1e-9, "0.000000001")]
    [InlineData(500, "500")]
    public void FromDouble_KeepsTheShortValue(double input, string expected)
        => Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), DecimalConvert.FromDouble(input));

    [Fact]
    public void FromDouble_RejectsNaN()
        => Assert.Throws<OverflowException>(() => DecimalConvert.FromDouble(double.NaN));
}
