using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces
{
    /// <summary>
    /// AnonChange (https://anonchange.com) — no-KYC instant exchange with its own reserves.
    /// Prices a pair via the signed partner API (POST /api/partner/v1/price).
    /// Supports both float and fixed quotes.
    /// </summary>
    public interface IAnonChangeClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
    { }
}
