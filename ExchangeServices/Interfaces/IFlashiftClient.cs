using ExchangeServices.Abstractions;

namespace ExchangeServices.Interfaces
{
    /// <summary>
    /// Flashift (https://flashift.app) — instant-exchange aggregator.
    /// One /getEstimatedAmount call returns an array of provider offers (floating AND fixed);
    /// we surface the best rate for the requested type. Privacy grade is "V" (varies) — the
    /// underlying provider differs per quote. Rate-limited to 10 req/min, so the client caches
    /// each direction's best rate for a short TTL to stay well under that.
    /// </summary>
    public interface IFlashiftClient : IExchangePriceApi, IExchangeBuyPriceApi, IRateType, IPrivacyLevel, IMinAmountUsd
    { }
}
