using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IExwellClient : IExchangePriceApi, IExchangeBuyPriceApi, IExchangeCurrencyApi, IPrivacyLevel, IMinAmountUsd
{ }
