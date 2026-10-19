namespace ExchangeServices.Implementations;

public sealed class AnonChangeOptions
{
    /// <summary>Partner API base. Every endpoint is a signed POST under /api/partner/v1.</summary>
    public string BaseUrl { get; set; } = "https://anonchange.com/api/partner/v1/";

    /// <summary>Partner API key (<c>X-API-KEY</c>). No key or secret → client returns null.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Partner API secret, used only to HMAC-sign requests. Keep it in appsettings.Production.json.</summary>
    public string ApiSecret { get; set; } = "";

    public string SiteName { get; set; } = "AnonChange";

    /// <summary>Public site (referral) URL shown/linked in the UI.</summary>
    public string? SiteUrl { get; set; } = "https://anonchange.com";

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 1;

    public string UserAgent { get; set; } = "Monerica/1.0";

    /// <summary>"float" (default) or "fixed".</summary>
    public string RateType { get; set; } = "float";

    /// <summary>Privacy grade shown on the board (own liquidity, guaranteed no KYC → 'A').</summary>
    public char PrivacyLevel { get; set; } = 'A';

    public decimal MinAmountUsd { get; set; } = 10m;

    /// <summary>Sell-side probe in XMR when the caller doesn't size one.</summary>
    public decimal SellProbeXmr { get; set; } = 1m;

    /// <summary>Buy-side probe for stablecoins when the caller doesn't size one.</summary>
    public decimal DefaultBuyProbeUsdt { get; set; } = 300m;
}
