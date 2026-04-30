using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NickFinance.Banking;
using Xunit;

namespace NickFinance.Banking.Tests;

public class BogRateProviderTests
{
    [Fact]
    public async Task Fetch_ReturnsEmpty_WhenEnvVarUnset()
    {
        // Snapshot + clear so this test is isolated from whatever the host
        // happens to have set.
        var saved = Environment.GetEnvironmentVariable("NICKFINANCE_BOG_API_URL");
        Environment.SetEnvironmentVariable("NICKFINANCE_BOG_API_URL", null);
        try
        {
            using var http = new HttpClient();
            var provider = new BogRateProvider(http, NullLogger<BogRateProvider>.Instance);
            var rates = await provider.FetchAsync(new DateOnly(2026, 4, 28));
            Assert.Empty(rates);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NICKFINANCE_BOG_API_URL", saved);
        }
    }

    [Fact]
    public async Task Fetch_ParsesJsonPayload_WhenEnvVarSet()
    {
        var saved = Environment.GetEnvironmentVariable("NICKFINANCE_BOG_API_URL");
        Environment.SetEnvironmentVariable("NICKFINANCE_BOG_API_URL", "https://fake.local/bog/{date}");
        try
        {
            var handler = new StubHandler((req, ct) =>
            {
                Assert.NotNull(req.RequestUri);
                Assert.Contains("2026-04-28", req.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
                var json = """
                {
                    "rates": [
                        { "currency": "USD", "midRate": 16.20 },
                        { "currency": "EUR", "midRate": 17.40 },
                        { "currency": "", "midRate": 99.0 },
                        { "currency": "GBP", "midRate": 0 }
                    ]
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            });
            using var http = new HttpClient(handler);
            var provider = new BogRateProvider(http, NullLogger<BogRateProvider>.Instance);
            var rates = await provider.FetchAsync(new DateOnly(2026, 4, 28));
            // Two valid rows survive — empty currency + zero midRate are filtered.
            Assert.Equal(2, rates.Count);
            Assert.Contains(rates, r => r.Currency == "USD" && r.MidRate == 16.20m);
            Assert.Contains(rates, r => r.Currency == "EUR" && r.MidRate == 17.40m);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NICKFINANCE_BOG_API_URL", saved);
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fn;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _fn(request, cancellationToken);
    }
}
