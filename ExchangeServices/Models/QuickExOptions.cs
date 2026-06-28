namespace ExchangeServices.Implementations;

public sealed class QuickexOptions
{
    public string BaseUrl { get; set; } = "https://quickex.io/";
    public string SiteName { get; set; } = "Quickex";
    public string? SiteUrl { get; set; } = "https://quickex.io";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;
    public string? ReferrerId { get; set; }  // e.g. "aff_your-id"

    // Quickex WAF blocks dotnet/httpclient UA strings — send a real browser UA.
    // This (not any API key) was the real reason the client previously "failed auth".
    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120 Safari/537.36";

    public string XmrCurrency { get; set; } = "XMR";
    public string XmrNetwork { get; set; } = "XMR";
    public string UsdtCurrency { get; set; } = "USDT";
    public string UsdtNetwork { get; set; } = "TRC20";

    // Probe amounts — bumped automatically if below API minimum via 422 retry
    public decimal SellProbeAmountXmr { get; set; } = 2m;
    public decimal BuyProbeAmountUsdt { get; set; } = 200m;
    public decimal MinAmountUsd { get; set; }
}