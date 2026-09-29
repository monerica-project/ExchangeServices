using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces;

/// <summary>
/// xmr2cex (https://xmr2cex.com) — a one-directional XMR → CEX-asset instant exchange:
/// you send XMR and receive btc/eth/sol/ltc/trx/usdt/usdc/bnb/hype. It is SELL-ONLY
/// (there is no asset → XMR "buy" side), so this client implements the sell price API but
/// not <see cref="IExchangeBuyPriceApi"/>.
/// </summary>
public interface IXmr2CexClient : IExchangePriceApi, IExchangeCurrencyApi, IRateType, IPrivacyLevel, IMinAmountUsd
{
}
