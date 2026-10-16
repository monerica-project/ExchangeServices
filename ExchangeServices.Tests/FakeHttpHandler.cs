using System.Net;
using System.Text;

namespace ExchangeServices.Tests;

/// <summary>Records every request (URL + body captured at send time) and answers via a callback.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, int, HttpResponseMessage> responder;

    public FakeHttpHandler(Func<HttpRequestMessage, string?, int, HttpResponseMessage> responder)
    {
        this.responder = responder;
    }

    public List<(string Url, string? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Reading disposed content throws ObjectDisposedException — exactly what a request that
        // reuses a StringContent across retries would hit.
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.ToString(), body));
        return responder(request, body, Requests.Count);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpClient Client(FakeHttpHandler handler, string baseAddress = "https://api.test/")
        => new(handler) { BaseAddress = new Uri(baseAddress) };
}
