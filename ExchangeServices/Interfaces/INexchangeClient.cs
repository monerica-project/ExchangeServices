using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface INexchangeClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{ }
