namespace ExchangeServices.Implementations;

public sealed class ElCapoOptions
{
    /// <summary>
    /// Trailing slash matters — the client uses relative paths off this: the partner
    /// rate lives at <c>api/partner/rate</c> (X-API-Key) and the public currency list
    /// at <c>api/v1/currencies</c>.
    /// </summary>
    public string BaseUrl { get; set; } = "https://elcapo.io/";

    /// <summary>Partner API key, sent in the <c>X-API-Key</c> header. No key → client returns null.</summary>
    public string ApiKey { get; set; } = "";

    public string SiteName { get; set; } = "El Capo";

    /// <summary>Public site (referral) URL shown/linked in the UI.</summary>
    public string? SiteUrl { get; set; } = "https://elcapo.io/?ref=monerica";

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 2;

    public string UserAgent { get; set; } = "Monerica/1.0";

    /// <summary>"float" (default) or "fixed" — the rate_type asked of /rate.</summary>
    public string RateType { get; set; } = "float";

    /// <summary>"A" — no-KYC verified direct exchange (KYC Level 0).</summary>
    public char PrivacyLevel { get; set; } = 'A';

    public decimal MinAmountUsd { get; set; } = 20m;

    public int CurrenciesCacheSeconds { get; set; } = 14400; // 4h
}
