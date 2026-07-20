using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces
{
    /// <summary>
    /// Swapzone (https://swapzone.io) — instant-exchange aggregator.
    /// Quotes the best offer across 15+ partners for a pair via GET /get-rate.
    /// Privacy grade is "V" (varies) — it's an aggregator, so the actual partner differs per quote.
    /// </summary>
    public interface ISwapzoneClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IRateType, IPrivacyLevel, IMinAmountUsd
    { }
}
