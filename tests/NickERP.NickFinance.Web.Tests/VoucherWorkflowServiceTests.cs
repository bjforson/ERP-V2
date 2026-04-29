using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Core.Enums;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Database;
using NickERP.NickFinance.Web.Services;
using NickERP.Platform.Tenancy;

namespace NickERP.NickFinance.Web.Tests;

/// <summary>
/// G2 — voucher workflow service tests covering the seven canonical
/// state-machine paths + actor authorization + ledger emission. Uses
/// the EF in-memory provider; the DbContext doesn't enforce CHECK
/// constraints under in-memory (rejected at the DB layer in prod), so
/// these tests focus on workflow logic, not constraint enforcement
/// (the latter is exercised by the Postgres-backed
/// <see cref="NickERP.NickFinance.Database.Tests.NickFinanceRlsIsolationTests"/>).
/// </summary>
public sealed class VoucherWorkflowServiceTests
{
    private const long TenantId = 1L;
    private static readonly Guid Custodian = Guid.NewGuid();
    private static readonly Guid Approver = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid OtherUser = Guid.NewGuid();

    private static (VoucherWorkflowService svc, NickFinanceDbContext db, CapturingEventPublisher events)
        BuildService(Guid actor)
    {
        var db = TestDb.Build();
        var events = new CapturingEventPublisher();
        var fx = new FakeFxRateLookup(1m);
        var baseCurrency = new StubBaseCurrencyLookup("GHS");
        var period = new PeriodLockService(db);
        var tenant = TestTenant.For(TenantId);
        var auth = new FakeAuthStateProvider(actor);
        var svc = new VoucherWorkflowService(
            db, events, fx, baseCurrency, period, tenant, auth,
            NullLogger<VoucherWorkflowService>.Instance);
        return (svc, db, events);
    }

    private static async Task<PettyCashBox> SeedBoxAsync(NickFinanceDbContext db, Guid? customCustodian = null, Guid? customApprover = null)
    {
        var box = new PettyCashBox
        {
            Code = "smk",
            Name = "smoke",
            CurrencyCode = "GHS",
            CustodianUserId = customCustodian ?? Custodian,
            ApproverUserId = customApprover ?? Approver,
            OpeningBalanceAmount = 1000m,
            OpeningBalanceCurrency = "GHS",
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = TenantId
        };
        db.Boxes.Add(box);
        await db.SaveChangesAsync();
        return box;
    }

    // -------- Happy path: full Request → Approve → Disburse → Reconcile -----

    [Fact]
    public async Task FullHappyPath_request_approve_disburse_reconcile_emits_correct_events()
    {
        // Stage 1: requester raises voucher.
        var (svcRequester, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svcRequester.RequestAsync(box.Id, "Office supplies", 100m);
        voucher.State.Should().Be(VoucherState.Request);
        voucher.SequenceNumber.Should().Be(1);
        events.Captured.Should().ContainSingle(e => e.EventType == "nickfinance.voucher.requested");

        // Stage 2: approver signs off.
        var (svcApprover, _, _) = BuildSharedDb(db, Approver, events);
        var approved = await svcApprover.ApproveAsync(voucher.Id);
        approved.State.Should().Be(VoucherState.Approve);
        approved.ApproverUserId.Should().Be(Approver);
        events.Captured.Should().Contain(e => e.EventType == "nickfinance.voucher.approved");

        // Stage 3: custodian disburses — emits Debit ledger event.
        var (svcCustodian, _, _) = BuildSharedDb(db, Custodian, events);
        var disbursed = await svcCustodian.DisburseAsync(voucher.Id);
        disbursed.State.Should().Be(VoucherState.Disburse);
        disbursed.DisbursedAmount.Should().Be(100m);
        events.Captured.Should().Contain(e => e.EventType == "nickfinance.voucher.disbursed");

        var ledger = await db.LedgerEvents.AsNoTracking().Where(le => le.VoucherId == voucher.Id).ToListAsync();
        ledger.Should().HaveCount(1);
        ledger[0].EventType.Should().Be(LedgerEventType.Disburse);
        ledger[0].Direction.Should().Be(LedgerDirection.Debit);
        ledger[0].AmountNative.Should().Be(100m);

        // Stage 4: requester reconciles with matching receipts → no Adjust event.
        var (svcReconcile, _, _) = BuildSharedDb(db, Requester, events);
        var reconciled = await svcReconcile.ReconcileAsync(voucher.Id, 100m);
        reconciled.State.Should().Be(VoucherState.Reconcile);
        events.Captured.Should().Contain(e => e.EventType == "nickfinance.voucher.reconciled");

        ledger = await db.LedgerEvents.AsNoTracking().Where(le => le.VoucherId == voucher.Id).ToListAsync();
        ledger.Should().HaveCount(1, "matching receipts → no Adjust event");
    }

    [Fact]
    public async Task Reconcile_with_under_spend_emits_Adjust_credit()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "Travel", 100m);
        var (sa, _, _) = BuildSharedDb(db, Approver, events); await sa.ApproveAsync(voucher.Id);
        var (sc, _, _) = BuildSharedDb(db, Custodian, events); await sc.DisburseAsync(voucher.Id);

        // Receipt total > disbursed → requester proved they spent more, box owes — Credit.
        var (sr, _, _) = BuildSharedDb(db, Requester, events);
        await sr.ReconcileAsync(voucher.Id, 120m);

        var adjust = await db.LedgerEvents.AsNoTracking()
            .Where(le => le.VoucherId == voucher.Id && le.EventType == LedgerEventType.Adjust)
            .SingleAsync();
        adjust.Direction.Should().Be(LedgerDirection.Credit);
        adjust.AmountNative.Should().Be(20m);
    }

    [Fact]
    public async Task Reconcile_with_over_spend_emits_Adjust_debit()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "Travel", 100m);
        var (sa, _, _) = BuildSharedDb(db, Approver, events); await sa.ApproveAsync(voucher.Id);
        var (sc, _, _) = BuildSharedDb(db, Custodian, events); await sc.DisburseAsync(voucher.Id);

        // Receipt total < disbursed → cash short — Debit.
        var (sr, _, _) = BuildSharedDb(db, Requester, events);
        await sr.ReconcileAsync(voucher.Id, 80m);

        var adjust = await db.LedgerEvents.AsNoTracking()
            .Where(le => le.VoucherId == voucher.Id && le.EventType == LedgerEventType.Adjust)
            .SingleAsync();
        adjust.Direction.Should().Be(LedgerDirection.Debit);
        adjust.AmountNative.Should().Be(20m);
    }

    // -------- Forbidden actor paths -----------------------------------

    [Fact]
    public async Task Approve_by_non_approver_throws_unauthorized()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "X", 10m);

        var (other, _, _) = BuildSharedDb(db, OtherUser, events);
        var act = () => other.ApproveAsync(voucher.Id);
        await act.Should().ThrowAsync<UnauthorizedVoucherActorException>()
            .Where(ex => ex.Action == "Approve");
    }

    [Fact]
    public async Task Disburse_by_non_custodian_throws_unauthorized()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "X", 10m);
        var (sa, _, _) = BuildSharedDb(db, Approver, events); await sa.ApproveAsync(voucher.Id);

        var (other, _, _) = BuildSharedDb(db, OtherUser, events);
        var act = () => other.DisburseAsync(voucher.Id);
        await act.Should().ThrowAsync<UnauthorizedVoucherActorException>()
            .Where(ex => ex.Action == "Disburse");
    }

    [Fact]
    public async Task Cancel_by_unrelated_user_throws_unauthorized()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "X", 10m);

        var (other, _, _) = BuildSharedDb(db, OtherUser, events);
        var act = () => other.CancelAsync(voucher.Id);
        await act.Should().ThrowAsync<UnauthorizedVoucherActorException>();
    }

    [Fact]
    public async Task Cancel_after_disburse_is_forbidden_by_state_machine()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "X", 10m);
        var (sa, _, _) = BuildSharedDb(db, Approver, events); await sa.ApproveAsync(voucher.Id);
        var (sc, _, _) = BuildSharedDb(db, Custodian, events); await sc.DisburseAsync(voucher.Id);

        var (sr, _, _) = BuildSharedDb(db, Requester, events);
        var act = () => sr.CancelAsync(voucher.Id);
        await act.Should().ThrowAsync<InvalidVoucherTransitionException>()
            .Where(ex => ex.From == VoucherState.Disburse && ex.To == VoucherState.Cancelled);
    }

    [Fact]
    public async Task Reject_records_reason_and_terminates()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "Frivolous", 999m);

        var (sa, _, _) = BuildSharedDb(db, Approver, events);
        var rejected = await sa.RejectAsync(voucher.Id, "Outside policy");

        rejected.State.Should().Be(VoucherState.Rejected);
        rejected.RejectedReason.Should().Be("Outside policy");
        events.Captured.Should().Contain(e => e.EventType == "nickfinance.voucher.rejected");
    }

    [Fact]
    public async Task Cancel_from_Request_state_terminates_voucher()
    {
        var (svc, db, events) = BuildService(Requester);
        var box = await SeedBoxAsync(db);
        var voucher = await svc.RequestAsync(box.Id, "X", 10m);

        var cancelled = await svc.CancelAsync(voucher.Id);
        cancelled.State.Should().Be(VoucherState.Cancelled);
        cancelled.CancelledAt.Should().NotBeNull();
        events.Captured.Should().Contain(e => e.EventType == "nickfinance.voucher.cancelled");
    }

    // -------- Box creation rules --------------------------------------

    [Fact]
    public async Task CreateBox_with_same_user_for_custodian_and_approver_throws()
    {
        var (svc, _, _) = BuildService(Requester);
        var same = Guid.NewGuid();
        var act = () => svc.CreateBoxAsync("c", "n", "GHS", same, same, 0m);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*separation-of-duties*");
    }

    [Fact]
    public async Task CreateBox_emits_box_created_event()
    {
        var (svc, _, events) = BuildService(Requester);
        var box = await svc.CreateBoxAsync("c", "n", "GHS", Custodian, Approver, 50m);
        box.Id.Should().NotBeEmpty();
        events.Captured.Should().Contain(e => e.EventType == "nickfinance.box.created");
    }

    // -------- FX missing rate --------------------------------------

    [Fact]
    public async Task Disburse_when_FX_rate_missing_throws_FxRateNotPublishedException()
    {
        var db = TestDb.Build();
        var events = new CapturingEventPublisher();
        var fx = new NullFxRateLookup();
        var baseCurrency = new StubBaseCurrencyLookup("USD"); // box.GHS != base.USD → needs rate
        var period = new PeriodLockService(db);
        var tenant = TestTenant.For(TenantId);

        var requesterAuth = new FakeAuthStateProvider(Requester);
        var requesterSvc = new VoucherWorkflowService(
            db, events, fx, baseCurrency, period, tenant, requesterAuth,
            NullLogger<VoucherWorkflowService>.Instance);

        var box = await SeedBoxAsync(db);
        // Request itself fails because RequestAsync also needs the FX
        // snapshot — verify we get the right exception type.
        var act = () => requesterSvc.RequestAsync(box.Id, "X", 10m);
        await act.Should().ThrowAsync<FxRateNotPublishedException>();
    }

    // -- helpers ------------------------------------------------------------

    private static (VoucherWorkflowService svc, NickFinanceDbContext db, CapturingEventPublisher events)
        BuildSharedDb(NickFinanceDbContext db, Guid actor, CapturingEventPublisher events)
    {
        var fx = new FakeFxRateLookup(1m);
        var baseCurrency = new StubBaseCurrencyLookup("GHS");
        var period = new PeriodLockService(db);
        var tenant = TestTenant.For(TenantId);
        var auth = new FakeAuthStateProvider(actor);
        var svc = new VoucherWorkflowService(
            db, events, fx, baseCurrency, period, tenant, auth,
            NullLogger<VoucherWorkflowService>.Instance);
        return (svc, db, events);
    }
}
