using NickFinance.PettyCash.Approvals;
using Xunit;

namespace NickFinance.Adapters.Tests;

public sealed class RoutingApprovalNotifierTests
{
    private sealed class RecordingNotifier : IApprovalNotifier
    {
        public string Channel { get; }
        public bool WasCalled { get; private set; }

        public RecordingNotifier(string channel) { Channel = channel; }

        public Task<NotifierResult> NotifyAsync(ApprovalNotification msg, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(new NotifierResult(true, Channel, $"id-{Channel}", null));
        }
    }

    private static ApprovalNotification Msg() => new(
        VoucherId: Guid.NewGuid(),
        VoucherNo: "PC-X-2026-1",
        AmountMinor: 1_000_00,
        CurrencyCode: "GHS",
        ApproverIdentifier: "+233244000111",
        Purpose: "test");

    [Fact]
    public async Task Real_token_routes_to_real_implementation()
    {
        var real = new RecordingNotifier("whatsapp");
        var noop = new RecordingNotifier("noop");
        var sut = new RoutingApprovalNotifier(real, noop, configuredToken: "EAAGm0PX...real-meta-bearer");

        var result = await sut.NotifyAsync(Msg());

        Assert.True(real.WasCalled);
        Assert.False(noop.WasCalled);
        Assert.Equal("whatsapp", sut.Channel);
        Assert.Equal("id-whatsapp", result.VendorMessageId);
    }

    [Fact]
    public async Task Placeholder_token_routes_to_noop()
    {
        var real = new RecordingNotifier("whatsapp");
        var noop = new RecordingNotifier("noop");
        var sut = new RoutingApprovalNotifier(real, noop, configuredToken: "PLACEHOLDER-set-real-token-here");

        var result = await sut.NotifyAsync(Msg());

        Assert.False(real.WasCalled);
        Assert.True(noop.WasCalled);
        Assert.Equal("noop", sut.Channel);
        Assert.Equal("id-noop", result.VendorMessageId);
    }
}
