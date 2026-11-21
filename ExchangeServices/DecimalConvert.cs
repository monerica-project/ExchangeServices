using System.Globalization;

namespace ExchangeServices;

/// <summary>
/// Converts a double to a decimal the way .NET 10 and earlier did: rounded to 15
/// significant digits. Since .NET 11 a plain cast keeps the full binary expansion
/// (0.3 becomes 0.2999999999999999888977697537), which is wrong for prices and amounts.
/// </summary>
public static class DecimalConvert
{
    public static decimal FromDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new OverflowException("Value is not a finite number.");
        }

        return decimal.Parse(
            value.ToString("G15", CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
    }
}
