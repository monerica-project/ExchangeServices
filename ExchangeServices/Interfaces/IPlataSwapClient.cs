using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IPlataSwapClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
{ }
