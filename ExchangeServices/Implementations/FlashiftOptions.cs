namespace ExchangeServices.Implementations;

public sealed class FlashiftOptions
{
    /// <summary>API base. Client uses relative paths off this (trailing slash added if missing).</summary>
    public string BaseUrl { get; set; } = "https://interfacev2.flashift.app/api/dev/v2/";

    /// <summary>Bearer token (sent as <c>Authorization: Bearer &lt;ApiKey&gt;</c>). No key → client returns null.</summary>
    public string ApiKey { get; set; } = "";

    public string SiteName { get; set; } = "Flashift";

    /// <summary>Public site (referral) URL shown/linked in the UI.</summary>
    public string? SiteUrl { get; set; } = "https://flashift.app";

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 1;

    public string UserAgent { get; set; } = "Monerica/1.0";

    /// <summary>"V" (varies) — aggregator; the underlying provider differs per quote.</summary>
    public char PrivacyLevel { get; set; } = 'V';

    public decimal MinAmountUsd { get; set; } = 20m;

    /// <summary>
    /// How long (seconds) each direction's best rate is cached. Flashift caps at 10 req/min;
    /// the board warms every ~15s and would otherwise fire ~6 calls/cycle (~24/min). Caching
    /// each of the 6 directions (3 pairs × buy/sell) for ~60s keeps it to ~6 calls/min.
    /// </summary>
    public int RateCacheSeconds { get; set; } = 60;

    /// <summary>Sell-side probe (XMR) when the caller doesn't size one.</summary>
    public decimal SellProbeXmr { get; set; } = 1m;

    /// <summary>Buy-side probe (quote currency) default for the USDT path (realistic, not the minimum).</summary>
    public decimal DefaultBuyProbeUsdt { get; set; } = 300m;
}
