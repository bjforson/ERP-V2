using NickFinance.AR;
using Xunit;

namespace NickFinance.Adapters.Tests;

public sealed class RoutingEvatProviderTests
{
    /// <summary>Tiny inline IEvatProvider that records being called.</summary>
    private sealed class RecordingProvider : IEvatProvider
    {
        public string Provider { get; }
        public bool WasCalled { get; private set; }

        public RecordingProvider(string name) { Provider = name; }

        public Task<EvatIssueResult> IssueAsync(EvatIssueRequest req, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(new EvatIssueResult(true, $"IRN-{Provider}", null, null));
        }
    }

    private static EvatIssueRequest Req() => new(
        InvoiceId: Guid.NewGuid(),
        InvoiceNo: "INV-1",
        CustomerName: "Acme",
        CustomerTin: null,
        InvoiceDate: new DateOnly(2026, 4, 28),
        CurrencyCode: "GHS",
        NetMinor: 100_00,
        LeviesMinor: 0,
        VatMinor: 0,
        GrossMinor: 100_00);

    [Theory]
    [InlineData("real-merchant-key-from-hubtel", true)]
    [InlineData("PLACEHOLDER-set-real-key-here", false)]
    [InlineData(null, false)]
    public async Task Routes_to_real_only_for_non_placeholder_key(string? key, bool expectReal)
    {
        var real = new RecordingProvider("real");
        var stub = new RecordingProvider("stub");
        var sut = new RoutingEvatProvider(real, stub, key);

        var result = await sut.IssueAsync(Req());

        Assert.Equal(expectReal, real.WasCalled);
        Assert.Equal(!expectReal, stub.WasCalled);
        Assert.True(result.Accepted);
        Assert.Equal(expectReal ? "real" : "stub", sut.Provider);
    }
}
