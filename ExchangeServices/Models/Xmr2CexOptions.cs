namespace ExchangeServices.Models;

/// <summary>
/// Options for the xmr2cex client. The ApiKey is a 16-digit account PIN sent as the
/// <c>x-api-key</c> header — it is a secret and must live in appsettings.Secrets.json
/// (git-ignored) / deployed secret config, never in the committed appsettings.json.
/// </summary>
public sealed class Xmr2CexOptions
{
    public string SiteName { get; set; } = "xmr2cex";

    public string? SiteUrl { get; set; } = "https://xmr2cex.com";

    public string BaseUrl { get; set; } = "https://xmr2cex.com";

    /// <summary>16-digit account PIN, sent as x-api-key. SECRET — set via Secrets config only.</summary>
    public string? ApiKey { get; set; }

    public string? UserAgent { get; set; } = "CryptoPriceNow/1.0";

    public int TimeoutSeconds { get; set; } = 12;

    public char PrivacyLevel { get; set; } = 'B';

    /// <summary>xmr2cex requires at least 1 XMR per swap, so the effective USD minimum is ~1 XMR.</summary>
    public decimal MinAmountUsd { get; set; } = 300m;
}
