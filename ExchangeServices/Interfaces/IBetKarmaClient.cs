using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

public interface IBetKarmaClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
{ }
