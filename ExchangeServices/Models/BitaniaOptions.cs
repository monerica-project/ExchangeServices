namespace ExchangeServices.Implementations;

/// <summary>
/// Bitania instant-swap API options. Docs: https://api.bitania.com/docs/
///
/// Auth (v2 HMAC, REQUIRED on every swap endpoint — including price/currencies):
///   X-API-KEY        : the public API key  (created via POST /api-keys, "bta_…")
///   X-API-SIGN       : HMAC-SHA256(secret, "{ts}\n{METHOD}\n{path}\n{sha256hex(body)}")
///   X-API-TIMESTAMP  : unix seconds
///   X-API-NONCE      : random per-request nonce
/// Both ApiKey and ApiSecret are required to get live quotes — leave blank and the
/// client no-ops (logs once). Keys live in gitignored config, never committed.
/// </summary>
public sealed class BitaniaOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;

    // Base host + version. Quotes hit {BaseUrl}/swap/price etc.
    public string BaseUrl { get; set; } = "https://api.bitania.com/v1";

    // Path prefix used INSIDE the HMAC canonical string. The signed path is
    // "{SignPathPrefix}/swap/price". Bitania signs the full request path, which
    // includes the /v1 version segment. If signatures are rejected, try "".
    public string SignPathPrefix { get; set; } = "/v1";

    public string SiteName { get; set; } = "Bitania";
    public string? SiteUrl { get; set; } = "https://swap.bitania.com/";
    public char PrivacyLevel { get; set; } = 'B';
    public int RequestTimeoutSeconds { get; set; } = 10;

    // Rate type for quotes: "float" (estimated) or "fixed" (locked 20 min).
    public string RateType { get; set; } = "float";

    // Buy-side probe, denominated in the quote currency (overridden per-quote by
    // PriceQuery.ProbeAmount when PriceService sets one).
    public decimal BuyProbeAmountUsdt { get; set; } = 100m;
    public decimal MinAmountUsd { get; set; }

    // ── Currency code mapping ────────────────────────────────────────────────
    // Bitania identifies each asset by a single `code` that folds in the network
    // (e.g. USDT on Tron is "USDTTRC", on Ethereum "USDTERC"). These defaults are
    // the conventional codes; VERIFY against POST /swap/currencies once keys are
    // available, since exact tickers are defined by Bitania's listing.
    public string XmrCode { get; set; } = "XMR";
    public string BtcCode { get; set; } = "BTC";
    public string EthCode { get; set; } = "ETH";
    public string UsdtTrcCode { get; set; } = "USDTTRC";
    public string UsdtErcCode { get; set; } = "USDTERC";
}
