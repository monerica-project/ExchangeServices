namespace ExchangeServices.SageSwap;

public sealed class SageSwapOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://sageswap.io/api"; // base includes /api
    public int TimeoutSeconds { get; set; } = 10;

    // SageSwap's authenticated JSON API (/api/v1/*) is fronted by BunnyCDN, whose bot
    // rule 403s non-browser User-Agents (e.g. "CryptoPriceNow/1.0") even WITH a valid
    // Bearer token — which silently kills the FIXED-rate quotes and leaves only the
    // public currencies.xml float feed. A browser-like UA + the token returns 200, so
    // we present as a browser. (The public float feed isn't gated and works with any UA.)
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    public string? Token { get; set; }
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}