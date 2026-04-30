using System.Net;
using NickFinance.PettyCash.Receipts;
using Xunit;

namespace NickFinance.Adapters.Tests;

public sealed class AzureFormRecognizerOcrEngineTests
{
    [Fact]
    public async Task Submit_401_returns_empty_result_without_throwing()
    {
        var handler = new FakeHttpHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":\"unauthorized\"}", System.Text.Encoding.UTF8, "application/json")
            }
        };
        var http = new HttpClient(handler);
        var sut = new AzureFormRecognizerOcrEngine(http, "https://nickerp-finance-di.cognitiveservices.azure.com", "bad-key");

        var result = await sut.RecogniseAsync(new byte[] { 0x01, 0x02, 0x03 }, "image/jpeg");

        Assert.Null(result.AmountMinor);
        Assert.Null(result.Date);
        Assert.Null(result.RawText);
        Assert.Equal((byte)0, result.Confidence);
    }
}
