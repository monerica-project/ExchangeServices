namespace ExchangeServices.Implementations;

public sealed class ExplaceOptions
{
    /// <summary>Base URL. Public endpoints under /public/, authed ones under /protected/.</summary>
    public string BaseUrl { get; set; } = "https://api.explace.io/";

    /// <summary>Partner API key, sent in the <c>X-API-KEY</c> header. No key → client returns null.</summary>
    public string ApiKey { get; set; } = "";

    public string SiteName { get; set; } = "Explace";

    /// <summary>Public site (referral) URL shown/linked in the UI.</summary>
    public string? SiteUrl { get; set; } = "https://explace.io";

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 2;

    public string UserAgent { get; set; } = "Monerica/1.0";

    /// <summary>"float" (default) or "fix" — the <c>details.type</c> asked of /estimate.</summary>
    public string RateType { get; set; } = "float";

    /// <summary>Privacy grade shown on the board (default 'C'; overridable via config).</summary>
    public char PrivacyLevel { get; set; } = 'C';

    public decimal MinAmountUsd { get; set; } = 20m;

    /// <summary>Sell-side probe size in XMR. Must be realistic — a near-minimum probe is
    /// fee-distorted and yields a wrong unit rate.</summary>
    public decimal SellProbeXmr { get; set; } = 1m;

    /// <summary>Buy-side probe (quote currency) used when the caller doesn't size one — the USDT path.</summary>
    public decimal DefaultBuyProbeUsdt { get; set; } = 300m;
}
