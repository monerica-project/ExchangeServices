namespace ExchangeServices.Abstractions;

public sealed record PriceQuery(AssetRef Base, AssetRef Quote, decimal? ProbeAmount = null, bool Fixed = false)
{
    public string Key => $"{Base.Key}->{Quote.Key}:{(Fixed ? "fix" : "flt")}";
}

// Fixed: when true, request a FIXED-rate quote (amount locked at quote time) from
// exchanges that support it; otherwise the default FLOATING quote. Defaults to
// false so existing callers (e.g. MoneroPriceNow) are unchanged.

// ProbeAmount: optional buy-side probe size, denominated in the Quote currency.
// Clients that quote the buy direction by sending a fixed amount and measuring
// how much XMR comes back should use `query.ProbeAmount ?? <their own default>`.
// PriceService sets this per quote (BTC/ETH) so the probe clears each exchange's
// minimum without blowing past its maximum. Null = use the client's own default
// (keeps the existing USDT behaviour untouched).
