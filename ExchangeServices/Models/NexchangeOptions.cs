namespace ExchangeServices.Models;

public sealed class NexchangeOptions
{
    // /currency/ is public, but /rate/ requires an API key sent as
    // "Authorization: ApiKey <key>". Leave empty to use only the currency list.
    public string ApiKey { get; set; } = "";

    // Optional affiliate/referral token sent as the "x-referral-token" header to
    // attribute rate quotes (and orders) to a referral code.
    public string? ReferralToken { get; set; }

    public string BaseUrl { get; set; } = "https://api.n.exchange/en/api/v2";
    public string SiteName { get; set; } = "n.exchange";
    public string? SiteUrl { get; set; } = "https://n.exchange";
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; } = 12;

    // Buy-side probe size in the quote currency (USDT) when ProbeAmount isn't supplied.
    public decimal BuyProbeAmountUsdt { get; set; } = 500m;

    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}
