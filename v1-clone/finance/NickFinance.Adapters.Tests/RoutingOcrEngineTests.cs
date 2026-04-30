using NickFinance.PettyCash.Receipts;
using Xunit;

namespace NickFinance.Adapters.Tests;

public sealed class RoutingOcrEngineTests
{
    private sealed class RecordingEngine : IOcrEngine
    {
        public string Vendor { get; }
        public bool WasCalled { get; private set; }

        public RecordingEngine(string vendor) { Vendor = vendor; }

        public Task<OcrResult> RecogniseAsync(byte[] content, string contentType, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(new OcrResult(123_45, new DateOnly(2026, 4, 28), Vendor, 90));
        }
    }

    [Fact]
    public async Task Real_key_routes_to_real_engine()
    {
        var real = new RecordingEngine("azure");
        var noop = new RecordingEngine("noop");
        var sut = new RoutingOcrEngine(real, noop, configuredKey: "real-azure-key-abcdef");

        var result = await sut.RecogniseAsync(new byte[] { 0xFF }, "image/jpeg");

        Assert.True(real.WasCalled);
        Assert.False(noop.WasCalled);
        Assert.Equal("azure", sut.Vendor);
        Assert.Equal("azure", result.RawText);
    }

    [Fact]
    public async Task Placeholder_key_routes_to_noop()
    {
        var real = new RecordingEngine("azure");
        var noop = new RecordingEngine("noop");
        var sut = new RoutingOcrEngine(real, noop, configuredKey: "PLACEHOLDER-azure-key");

        var result = await sut.RecogniseAsync(new byte[] { 0xFF }, "image/jpeg");

        Assert.False(real.WasCalled);
        Assert.True(noop.WasCalled);
        Assert.Equal("noop", sut.Vendor);
        Assert.Equal("noop", result.RawText);
    }
}
