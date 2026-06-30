namespace ExchangeServices.Implementations;

public sealed class BitXChangeOptions
{
    public string  ApiKey                { get; set; } = string.Empty;
    public string  BaseUrl               { get; set; } = "https://api.bitxchange.io";
    public string  SiteName              { get; set; } = "BitXChange";
    public string? SiteUrl               { get; set; } = "https://bitxchange.io";
    public char    PrivacyLevel          { get; set; } = 'B';
    public int     RequestTimeoutSeconds { get; set; } = 10;

    public string  XmrSymbol         { get; set; } = "XMR";
    public string  XmrNetwork        { get; set; } = "XMR";
    public string  UsdtSymbol        { get; set; } = "USDT";
    public string  UsdtNetwork       { get; set; } = "TRC20";
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }

    // GetCurrenciesAsync enumerates the coin universe from /crypto/limits and then
    // fetches each coin's networks via /crypto?coin=NAME. This bounds the number of
    // concurrent per-coin requests against the API.
    public int CurrencyFetchConcurrency { get; set; } = 8;
}
