namespace ExchangeServices.Models;

public sealed class ExwellOptions
{
    // Exwell requires a key on EVERY request (even /currencies); leave empty to disable.
    public string ApiKey { get; set; } = "";

    public string BaseUrl { get; set; } = "https://exwell.io/v1/api";
    public string SiteName { get; set; } = "Exwell";
    public string? SiteUrl { get; set; } = "https://exwell.io";
    public string? UserAgent { get; set; }
    public int TimeoutSeconds { get; set; } = 12;

    // Codes as returned by /currencies (verify against the live list once keyed).
    public string XmrCode { get; set; } = "XMR";
    // USDT via ERC20 (Tron withdrawal fees run higher here).
    public string UsdtCode { get; set; } = "USDT";

    // Buy-side probe in the quote currency (USDT) when ProbeAmount isn't supplied.
    // Must clear Exwell's USDT minimum (~600) — below it there's no tradeable rate.
    public decimal BuyProbeAmountUsdt { get; set; } = 650m;

    public char PrivacyLevel { get; set; } = 'B';
    public decimal MinAmountUsd { get; set; }
}
