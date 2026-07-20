namespace ExchangeServices.Implementations;

public sealed class SwapzoneOptions
{
    /// <summary>Trailing slash matters — the client uses relative paths off this.</summary>
    public string BaseUrl { get; set; } = "https://api.swapzone.io/v1/exchange/";

    /// <summary>Partner API key, sent in the <c>x-api-key</c> header. No key → client returns null.</summary>
    public string ApiKey { get; set; } = "";

    public string SiteName { get; set; } = "Swapzone";

    /// <summary>Public site (referral) URL shown/linked in the UI.</summary>
    public string? SiteUrl { get; set; } = "https://swapzone.io";

    public int RequestTimeoutSeconds { get; set; } = 12;
    public int RetryCount { get; set; } = 2;

    /// <summary>Swapzone's WAF 401s requests with no User-Agent — always send one.</summary>
    public string UserAgent { get; set; } = "Monerica/1.0";

    /// <summary>"floating" (default), "fixed" or "all" — the rateType asked of /get-rate.</summary>
    public string RateType { get; set; } = "floating";

    /// <summary>"V" (varies) — aggregator; the underlying partner differs per quote.</summary>
    public char PrivacyLevel { get; set; } = 'V';

    public decimal MinAmountUsd { get; set; } = 20m;

    public int CurrenciesCacheSeconds { get; set; } = 14400; // 4h
}
