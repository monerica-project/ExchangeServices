namespace ExchangeServices.Implementations;

public sealed class SwapuzOptions
{
    public string  BaseUrl               { get; set; } = "https://api.swapuz.com";
    public string  ApiKey                { get; set; } = "";
    public string  SiteName              { get; set; } = "Swapuz";
    public string? SiteUrl               { get; set; } = "https://swapuz.com";
    public int     RequestTimeoutSeconds { get; set; } = 10;
    // Required: api.swapuz.com returns 403 for requests with no User-Agent.
    public string  UserAgent             { get; set; } = "Monerica/1.0";
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }
}
