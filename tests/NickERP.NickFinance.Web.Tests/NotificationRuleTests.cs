using System.Text.Json;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Services.NotificationRules;

namespace NickERP.NickFinance.Web.Tests;

/// <summary>
/// G2 §7 — tests for the three NickFinance notification rules. Each
/// rule is a pure projection of one DomainEventRow into zero or more
/// <c>audit.notifications</c> rows; tests construct the row directly.
/// </summary>
public sealed class NotificationRuleTests
{
    [Fact]
    public async Task VoucherApprovalRequested_notifies_box_approver()
    {
        var rule = new VoucherApprovalRequestedRule();
        var approver = Guid.NewGuid();
        var voucherId = Guid.NewGuid();

        var evt = BuildRow(
            "nickfinance.voucher.requested",
            "PettyCashVoucher",
            voucherId.ToString(),
            new { Id = voucherId, BoxApproverUserId = approver, RequestedByUserId = Guid.NewGuid() });

        var produced = await rule.ProjectAsync(evt);

        produced.Should().HaveCount(1);
        produced[0].UserId.Should().Be(approver);
        produced[0].Link.Should().Contain(voucherId.ToString());
        produced[0].Title.Should().Contain("approval");
    }

    [Fact]
    public async Task VoucherApprovalRequested_emits_zero_when_no_approver_in_payload()
    {
        var rule = new VoucherApprovalRequestedRule();
        var evt = BuildRow("nickfinance.voucher.requested", "PettyCashVoucher", "abc",
            new { Id = Guid.NewGuid() }); // no BoxApproverUserId

        var produced = await rule.ProjectAsync(evt);
        produced.Should().BeEmpty();
    }

    [Fact]
    public async Task VoucherApproved_notifies_requester()
    {
        var rule = new VoucherApprovedRule();
        var requester = Guid.NewGuid();
        var voucherId = Guid.NewGuid();

        var evt = BuildRow(
            "nickfinance.voucher.approved",
            "PettyCashVoucher",
            voucherId.ToString(),
            new { Id = voucherId, ApproverUserId = Guid.NewGuid(), RequestedByUserId = requester });

        var produced = await rule.ProjectAsync(evt);

        produced.Should().HaveCount(1);
        produced[0].UserId.Should().Be(requester);
        produced[0].Link.Should().Contain(voucherId.ToString());
        produced[0].Title.Should().Contain("approved");
    }

    [Fact]
    public async Task VoucherDisbursed_notifies_requester()
    {
        var rule = new VoucherDisbursedRule();
        var requester = Guid.NewGuid();
        var voucherId = Guid.NewGuid();

        var evt = BuildRow(
            "nickfinance.voucher.disbursed",
            "PettyCashVoucher",
            voucherId.ToString(),
            new { Id = voucherId, RequestedByUserId = requester, CustodianUserId = Guid.NewGuid() });

        var produced = await rule.ProjectAsync(evt);

        produced.Should().HaveCount(1);
        produced[0].UserId.Should().Be(requester);
        produced[0].Link.Should().Contain(voucherId.ToString());
        produced[0].Title.Should().Contain("disbursed");
    }

    [Fact]
    public async Task VoucherDisbursed_emits_zero_when_no_requester_in_payload()
    {
        var rule = new VoucherDisbursedRule();
        var evt = BuildRow("nickfinance.voucher.disbursed", "PettyCashVoucher", "abc", new { });
        var produced = await rule.ProjectAsync(evt);
        produced.Should().BeEmpty();
    }

    [Fact]
    public void EventType_constants_match_workflow_emitter()
    {
        // Defence-in-depth: if anyone renames the event types in the
        // workflow service, these tests catch it because the rule type
        // strings would drift.
        new VoucherApprovalRequestedRule().EventType.Should().Be("nickfinance.voucher.requested");
        new VoucherApprovedRule().EventType.Should().Be("nickfinance.voucher.approved");
        new VoucherDisbursedRule().EventType.Should().Be("nickfinance.voucher.disbursed");
    }

    private static NickERP.Platform.Audit.Database.DomainEventRow BuildRow(
        string eventType, string entityType, string entityId, object payload)
    {
        return new NickERP.Platform.Audit.Database.DomainEventRow
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            Payload = JsonDocument.Parse(JsonSerializer.Serialize(payload)),
            OccurredAt = DateTimeOffset.UtcNow,
            IngestedAt = DateTimeOffset.UtcNow,
            TenantId = 1L,
            ActorUserId = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid().ToString("N")
        };
    }
}
