using System.Net;
using NickFinance.AR;
using Xunit;

namespace NickFinance.Adapters.Tests;

public sealed class HubtelEvatProviderTests
{
    private static EvatIssueRequest Req() => new(
        InvoiceId: Guid.NewGuid(),
        InvoiceNo: "INV-2026-001",
        CustomerName: "Acme Ltd",
        CustomerTin: "C0001234567",
        InvoiceDate: new DateOnly(2026, 4, 28),
        CurrencyCode: "GHS",
        NetMinor: 100_00,
        LeviesMinor: 5_00,
        VatMinor: 12_00,
        GrossMinor: 117_00);

    [Fact]
    public async Task Issue_200_with_Irn_returns_Accepted()
    {
        var handler = new FakeHttpHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"irn\":\"IRN-REAL-12345\",\"qrPayload\":\"qr-data\"}", System.Text.Encoding.UTF8, "application/json")
            }
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://evat.hubtel.com") };
        var sut = new HubtelEvatProvider(http);

        var result = await sut.IssueAsync(Req());

        Assert.True(result.Accepted);
        Assert.Equal("IRN-REAL-12345", result.Irn);
        Assert.Equal("qr-data", result.QrPayload);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Issue_500_returns_Rejected_with_reason()
    {
        var handler = new FakeHttpHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("upstream boom", System.Text.Encoding.UTF8, "text/plain")
            }
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://evat.hubtel.com") };
        var sut = new HubtelEvatProvider(http);

        var result = await sut.IssueAsync(Req());

        Assert.False(result.Accepted);
        Assert.Null(result.Irn);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("500", result.FailureReason);
    }

    [Fact]
    public async Task Issue_when_http_throws_returns_Rejected_unreachable()
    {
        var handler = new FakeHttpHandler
        {
            Respond = _ => throw new HttpRequestException("connect refused")
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://evat.hubtel.com") };
        var sut = new HubtelEvatProvider(http);

        var result = await sut.IssueAsync(Req());

        Assert.False(result.Accepted);
        Assert.Null(result.Irn);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("unreachable", result.FailureReason);
    }
}
