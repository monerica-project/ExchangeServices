using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces
{
    /// <summary>
    /// Explace (https://explace.io) — no-account instant swap that auto-routes a pair
    /// across major exchange liquidity (Kucoin/Gate/Mexc/Bybit) with <c>provider:null</c>.
    /// Prices a pair via POST /protected/swap/estimate.
    /// Privacy grade is "V" (varies) — the underlying provider differs per quote.
    /// Supports both floating and fixed estimates (details.type = "float" / "fix").
    /// </summary>
    public interface IExplaceClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
    { }
}
