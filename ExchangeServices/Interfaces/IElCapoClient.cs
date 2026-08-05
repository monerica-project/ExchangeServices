using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces
{
    /// <summary>
    /// El Capo (https://elcapo.io) — no-KYC instant exchange (KYC Level 0, Tor + no-JS).
    /// Rates via the partner GET /api/partner/rate (X-API-Key header); the currency list
    /// via the public GET /api/v1/currencies. Supports both float and fixed rate types.
    /// Privacy grade "A".
    /// </summary>
    public interface IElCapoClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IRateType, IPrivacyLevel, IMinAmountUsd
    { }
}
