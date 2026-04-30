using System.Net;

namespace NickFinance.Adapters.Tests;

/// <summary>
/// Fake <see cref="HttpMessageHandler"/> for unit tests of adapters that
/// take an <see cref="HttpClient"/>. Captures the most recent request for
/// assertions and lets the test author drive the response.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; init; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        LastRequest = req;
        return Task.FromResult(Respond(req));
    }
}
