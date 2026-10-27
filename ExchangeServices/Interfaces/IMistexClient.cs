using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IMistexClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
{ }
