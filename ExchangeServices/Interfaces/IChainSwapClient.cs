using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IChainSwapClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
{ }
