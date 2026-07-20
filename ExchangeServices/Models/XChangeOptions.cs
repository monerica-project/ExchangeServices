namespace ExchangeServices.Models;

public sealed class XChangeOptions
{
    public string SiteUrl { get; set; }
    public string SiteName { get; set; }
    public string BaseUrl { get; set; } = "https://xchange.me/api/v1";
    public int TimeoutSeconds { get; set; } = 10;
    public string UserAgent { get; set; } = "CryptoPriceNow/1.0";
    public char PrivacyLevel { get; set; }
    public decimal MinAmountUsd { get; set; }

    /// <summary>
    /// Absolute path to the bundled <c>curl-impersonate</c> wrapper (e.g. curl_chrome123) on the
    /// server. xChange.me is behind a Cloudflare TLS-fingerprint challenge that a normal HttpClient
    /// can't pass, so requests are made through this. Empty/missing → client disabled (returns null).
    /// </summary>
    public string CurlImpersonatePath { get; set; } = "";

    /// <summary>SELL probe: XMR sent when quoting xmr→quote (realistic, above pair minimums).</summary>
    public decimal SellProbeXmr { get; set; } = 1m;

    /// <summary>BUY probe fallback (quote units) when the caller doesn't size one per quote.</summary>
    public decimal DefaultBuyProbe { get; set; } = 0.01m;

    /// <summary>
    /// xChange silently drops (hangs) every <c>to_currency=xmr</c> request from datacenter IPs
    /// (buying XMR), while selling XMR works. Set true on servers behind that block to fail those
    /// quotes instantly instead of waiting out the timeout on every "into-XMR" search.
    /// </summary>
    public bool BlockToXmr { get; set; }
}
