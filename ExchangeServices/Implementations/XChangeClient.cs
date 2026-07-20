using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExchangeServices.Abstractions;
using ExchangeServices.Interfaces;
using ExchangeServices.Models;
using Microsoft.Extensions.Options;

namespace ExchangeServices.Implementations;

/// <summary>
/// xChange.me (https://xchange.me) — no-account, no-KYC instant exchanger.
///
/// The entire site (API included) sits behind Cloudflare's managed challenge, which
/// gates on the TLS/JA3 fingerprint. A normal HttpClient (or curl) is served a 403
/// "Just a moment…" page, so we fetch through a bundled <c>curl-impersonate</c> binary
/// that presents a real Chrome TLS handshake. If <see cref="XChangeOptions.CurlImpersonatePath"/>
/// isn't configured / present, the client is effectively disabled (returns null).
///
/// Pairs: the API only lets you SEND ~10 coins (from-list incl. xmr/btc/eth, NO usdt) but
/// RECEIVE 300+ (to-list incl. usdt). So SELL xmr→{btc,eth} and BUY {btc,eth}→xmr both work;
/// usdt is sell-only (not sendable) so it is excluded upstream via PriceService.QuoteSupport
/// (we only surface a pair when both directions work). /exchange/estimate returns a
/// direction-specific <c>rate</c> (spread already baked in) with estimate = rate×amount.
/// </summary>
public sealed class XChangeClient : IXChangeClient
{
    private readonly HttpClient http; // kept for DI shape; live fetch goes through curl-impersonate
    private readonly XChangeOptions opt;

    public string  ExchangeKey => "xchange";
    public string  SiteName    => opt.SiteName;
    public string? SiteUrl     => opt.SiteUrl;
    public decimal MinAmountUsd => opt.MinAmountUsd;
    public char PrivacyLevel => opt.PrivacyLevel;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public XChangeClient(HttpClient http, IOptions<XChangeOptions> options)
    {
        this.http = http;
        this.opt = options.Value;
    }

    // SELL: send `SellProbeXmr` XMR → receive quote. Price = estimate / amount (quote per 1 XMR).
    public async Task<PriceResult?> GetSellPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = Code(query.Base);
        var to = Code(query.Quote);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;
        // Into-XMR quotes are dropped by xChange from this server's IP — don't hang on them.
        if (opt.BlockToXmr && string.Equals(to, "xmr", StringComparison.OrdinalIgnoreCase)) return null;

        var probe = opt.SellProbeXmr > 0 ? opt.SellProbeXmr : 1m;
        var dto = await EstimateAsync(from, to, probe, ct);
        if (dto?.Estimate is null or <= 0) return null;

        var price = dto.Estimate.Value / probe;
        return price <= 0 ? null : Make(query, price);
    }

    // BUY: send `probe` quote → receive XMR. Price = amount / xmrReceived (quote per 1 XMR).
    // The site sizes the probe per quote (BTC≈0.01, ETH≈0.3). USDT can't be sent → null.
    public async Task<PriceResult?> GetBuyPriceAsync(PriceQuery query, CancellationToken ct = default)
    {
        var from = Code(query.Quote);
        var to = Code(query.Base);
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return null;
        // Buying XMR (to=xmr) is dropped from this server's IP — fail fast rather than hang.
        if (opt.BlockToXmr && string.Equals(to, "xmr", StringComparison.OrdinalIgnoreCase)) return null;

        var probe = query.ProbeAmount is > 0 ? query.ProbeAmount!.Value : opt.DefaultBuyProbe;
        if (probe <= 0) return null;

        var dto = await EstimateAsync(from, to, probe, ct);
        if (dto?.Estimate is null or <= 0) return null; // usdt (not sendable) lands here

        var quotePerXmr = probe / dto.Estimate.Value;
        return quotePerXmr <= 0 ? null : Make(query, quotePerXmr);
    }

    // currencies: union of from/to. Network is unknown from the API; leave it blank so the
    // price service's id-resolution falls back to the plain lowercase ticker (== xChange's code).
    public async Task<IReadOnlyList<ExchangeCurrency>> GetCurrenciesAsync(CancellationToken ct = default)
    {
        var from = await GetCurrencyListAsync("/currencies/from", ct);
        var to = await GetCurrencyListAsync("/currencies/to", ct);
        if (from.Count == 0 && to.Count == 0) return Array.Empty<ExchangeCurrency>();

        return from
            .Union(to, StringComparer.OrdinalIgnoreCase)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => new ExchangeCurrency(
                ExchangeId: x.Trim().ToLowerInvariant(),
                Ticker: x.Trim().ToUpperInvariant(),
                Network: ""))
            .OrderBy(x => x.Ticker)
            .ToList();
    }

    // xChange uses plain lowercase tickers (xmr, btc, eth); id-resolution never matches (unknown
    // network), so this reliably yields the code the /exchange endpoints want.
    private static string Code(AssetRef a)
    {
        var id = a.ExchangeId;
        if (!string.IsNullOrWhiteSpace(id)) return id!.Trim().ToLowerInvariant();
        return (a.Ticker ?? "").Trim().ToLowerInvariant();
    }

    private async Task<EstimateDto?> EstimateAsync(string from, string to, decimal amount, CancellationToken ct)
    {
        var url =
            "/exchange/estimate" +
            $"?from_currency={Uri.EscapeDataString(from)}" +
            $"&to_currency={Uri.EscapeDataString(to)}" +
            $"&amount={amount.ToString("0.########", CultureInfo.InvariantCulture)}";

        var raw = await FetchAsync(url, ct);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<EstimateDto>(raw, JsonOpts); }
        catch { return null; }
    }

    private async Task<IReadOnlyList<string>> GetCurrencyListAsync(string path, CancellationToken ct)
    {
        var raw = await FetchAsync(path, ct);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<List<string>>(raw, JsonOpts) ?? (IReadOnlyList<string>)Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    // Runs the bundled curl-impersonate wrapper so Cloudflare sees a real Chrome TLS fingerprint.
    private async Task<string?> FetchAsync(string relativePath, CancellationToken ct)
    {
        var exe = opt.CurlImpersonatePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null; // not deployed → disabled

        var timeout = Math.Clamp(opt.TimeoutSeconds, 3, 60);
        var url = opt.BaseUrl.TrimEnd('/') + relativePath;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("--max-time");
        psi.ArgumentList.Add(timeout.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(url);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeout + 3));

        Process? p = null;
        try
        {
            p = new Process { StartInfo = psi };
            if (!p.Start()) return null;
            var stdout = p.StandardOutput.ReadToEndAsync(linked.Token);
            await p.WaitForExitAsync(linked.Token);
            var body = await stdout;
            return p.ExitCode == 0 ? body : null;
        }
        catch (OperationCanceledException)
        {
            try { p?.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            p?.Dispose();
        }
    }

    private PriceResult Make(PriceQuery q, decimal price) =>
        new(ExchangeKey, q.Base, q.Quote, price, DateTimeOffset.UtcNow, null, null);

    private sealed class EstimateDto
    {
        [JsonPropertyName("rate")] public decimal? Rate { get; set; }
        [JsonPropertyName("estimate")] public decimal? Estimate { get; set; }
        [JsonPropertyName("fee_from")] public decimal? FeeFrom { get; set; }
        [JsonPropertyName("fee_to")] public decimal? FeeTo { get; set; }
    }
}
