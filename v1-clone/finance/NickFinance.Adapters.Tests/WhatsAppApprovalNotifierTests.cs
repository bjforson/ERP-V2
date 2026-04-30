using System.Net;
using NickFinance.PettyCash.Approvals;
using Xunit;

namespace NickFinance.Adapters.Tests;

public sealed class WhatsAppApprovalNotifierTests
{
    private static ApprovalNotification Msg(string approver = "+233244111222") => new(
        VoucherId: Guid.NewGuid(),
        VoucherNo: "PC-TEMA-2026-00421",
        AmountMinor: 12_345_67,
        CurrencyCode: "GHS",
        ApproverIdentifier: approver,
        Purpose: "Site fuel run",
        TenantId: 1);

    [Fact]
    public async Task Notify_200_with_message_id_marks_delivered()
    {
        var handler = new FakeHttpHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"messages\":[{\"id\":\"wamid.HBgL\"}]}", System.Text.Encoding.UTF8, "application/json")
            }
        };
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = new WhatsAppApprovalNotifier(http, "phone-number-id-123");

        var result = await sut.NotifyAsync(Msg());

        Assert.True(result.Delivered);
        Assert.Equal("whatsapp", result.Channel);
        Assert.Equal("wamid.HBgL", result.VendorMessageId);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Notify_with_empty_approver_phone_returns_failure()
    {
        var handler = new FakeHttpHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://graph.facebook.com") };
        var sut = new WhatsAppApprovalNotifier(http, "phone-number-id-123");

        var result = await sut.NotifyAsync(Msg(approver: ""));

        Assert.False(result.Delivered);
        Assert.NotNull(result.FailureReason);
        Assert.Null(handler.LastRequest); // never reached out
    }
}
