namespace ExchangeServices.Abstractions;

/// <summary>
/// Shared buy-side probe sizing. A buy quote is taken by sending a probe amount of the
/// QUOTE currency and measuring how much base (XMR) comes back, so the probe must be
/// denominated in the quote currency: "500" is a sane USDT probe but 500 BTC/ETH blows
/// past every exchange's maximum (and silently nulls the buy side).
///
/// Use <see cref="BuyProbe"/> instead of hardcoding a USD-sized constant, and divide the
/// result by the probe that was actually sent.
/// </summary>
public static class ProbeSizing
{
    private static readonly HashSet<string> StableTickers =
        new(StringComparer.OrdinalIgnoreCase) { "USDT", "USDC", "DAI", "USD", "BUSD", "TUSD", "USDD", "USDP" };

    /// <summary>True when the ticker is a ~$1 dollar stablecoin.</summary>
    public static bool IsUsdStable(string? ticker)
        => StableTickers.Contains((ticker ?? "").Trim());

    /// <summary>
    /// Buy-side probe in QUOTE units: the caller's <see cref="PriceQuery.ProbeAmount"/> when it
    /// is set and positive, otherwise <see cref="DefaultBuyProbe"/> for the quote currency.
    /// Rounded to 8 dp (some APIs reject more precision), never 0.
    /// </summary>
    public static decimal BuyProbe(PriceQuery query, decimal stableDefault)
    {
        if (query.ProbeAmount is decimal p && p > 0m)
        {
            return Round(p);
        }

        return DefaultBuyProbe(query.Quote, stableDefault);
    }

    /// <summary>
    /// Fallback probe for a quote currency when the caller didn't size one: the client's own
    /// stablecoin default for USD stables, otherwise a modest amount of the quote coin itself
    /// (same order of magnitude as the stablecoin defaults: ~0.01 BTC / ~0.3 ETH, the sizes
    /// MoneroPriceNow's PriceService used before it priced probes in USD). Any other coin
    /// falls back to 1 unit — the same convention the sell side uses for the base.
    /// </summary>
    public static decimal DefaultBuyProbe(AssetRef quote, decimal stableDefault)
    {
        var ticker = (quote.Ticker ?? "").Trim().ToUpperInvariant();
        if (IsUsdStable(ticker))
        {
            return stableDefault > 0m ? stableDefault : 500m;
        }

        return ticker switch
        {
            "BTC" => 0.01m,
            "ETH" => 0.3m,
            _ => 1m,
        };
    }

    /// <summary>Rounds a probe to 8 dp; a value that would round to 0 is kept at the smallest 8-dp unit.</summary>
    public static decimal Round(decimal v)
    {
        var r = Math.Round(v, 8, MidpointRounding.AwayFromZero);
        return r > 0m ? r : (v > 0m ? 0.00000001m : 0m);
    }
}
