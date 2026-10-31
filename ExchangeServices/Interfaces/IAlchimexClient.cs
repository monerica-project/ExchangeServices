using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IAlchimexClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
{ }
