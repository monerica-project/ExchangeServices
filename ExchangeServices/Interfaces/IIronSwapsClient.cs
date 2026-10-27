using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IIronSwapsClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
{ }
