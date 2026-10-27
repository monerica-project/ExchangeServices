using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ExchangeServices.Implementations;

/// <summary>Small shared helpers for the simple keyless JSON quote clients.</summary>
internal static class HttpJson
{
    /// <summary>GET a relative URL; returns the body on 2xx, otherwise null (timeouts and network errors too).</summary>
    public static Task<string?> GetAsync(HttpClient http, string url, int timeoutSeconds, CancellationToken ct)
        => SendAsync(http, new HttpRequestMessage(HttpMethod.Get, url), timeoutSeconds, ct);

    /// <summary>POST a JSON body; returns the body on 2xx, otherwise null.</summary>
    public static Task<string?> PostJsonAsync(HttpClient http, string url, object body, int timeoutSeconds, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return SendAsync(http, req, timeoutSeconds, ct);
    }

    public static async Task<string?> SendAsync(HttpClient http, HttpRequestMessage req, int timeoutSeconds, CancellationToken ct)
    {
        using (req)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 2, 60)));
            try
            {
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (HttpRequestException) { return null; }
        }
    }

    /// <summary>Reads a number that may be sent as a JSON number or a string. Null when missing or unparseable.</summary>
    public static decimal? Dec(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetDecimal(out var d) ? d : null,
            JsonValueKind.String => decimal.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null,
            _ => null,
        };
    }
}
