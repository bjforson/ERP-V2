using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using NickFinance.PettyCash;
using Xunit;

namespace NickFinance.PettyCash.Tests;

[Collection("PettyCash")]
public class PettyCashServiceTests
{
    private readonly PettyCashFixture _fx;
    public PettyCashServiceTests(PettyCashFixture fx) => _fx = fx;

    // ------------------------------------------------------------------
    // Happy path: full chain
    // ------------------------------------------------------------------

    [Fact]
    public async Task EndToEnd_CreateFloat_Submit_Approve_Disburse_PostsBalancedJournal()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var writer = new LedgerWriter(lg);
        var svc = new PettyCashService(pc, writer);

        // 1. Custodian provisioned with a GHS 5,000 float.
        var siteId = Guid.NewGuid();
        var custodian = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(siteId, custodian, Ghs(500_000), admin);
        Assert.True(fl.IsActive);
        Assert.Equal(500_000, fl.FloatAmountMinor);

        // 2. Requester (a different user from custodian + approver) submits
        //    a GHS 250 transport voucher.
        var requester = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            FloatId: fl.FloatId,
            RequesterUserId: requester,
            Category: VoucherCategory.Transport,
            Purpose: "Trip to Tema port to collect docs",
            Amount: Ghs(25_000),
            Lines: new[]
            {
                new VoucherLineInput("Cab Tema -> office", Ghs(15_000)),
                new VoucherLineInput("Return cab", Ghs(10_000))
            }));
        Assert.Equal(VoucherStatus.Submitted, v.Status);
        Assert.StartsWith("PC-", v.VoucherNo);

        // 3. Approver approves the voucher in full.
        var approved = await svc.ApproveVoucherAsync(v.VoucherId, approver, null, "OK to proceed");
        Assert.Equal(VoucherStatus.Approved, approved.Status);
        Assert.Equal(25_000, approved.AmountApprovedMinor);
        Assert.Equal(approver, approved.DecidedByUserId);

        // 4. Custodian disburses → kernel post.
        var effective = new DateOnly(2026, 4, 15);
        var disbursed = await svc.DisburseVoucherAsync(v.VoucherId, custodian, effective, period.PeriodId);
        Assert.Equal(VoucherStatus.Disbursed, disbursed.Status);
        Assert.NotNull(disbursed.LedgerEventId);
        Assert.Equal(custodian, disbursed.DisbursedByUserId);

        // 5. Ledger reflects the journal — verified structurally on the
        //    posted event below (#6). We avoid asserting on
        //    GetAccountBalanceAsync() here because other tests in the
        //    same xunit collection write to the same accounts in the
        //    shared fixture DB; balance-by-asOf would see their posts.

        // 6. The journal is the one we expect.
        await using var lg2 = _fx.CreateLedger();
        var posted = await lg2.Events
            .Include(e => e.Lines)
            .FirstAsync(e => e.EventId == disbursed.LedgerEventId);
        Assert.Equal("petty_cash", posted.SourceModule);
        Assert.Equal("Voucher", posted.SourceEntityType);
        Assert.Equal(v.VoucherId.ToString("N"), posted.SourceEntityId);
        Assert.Equal(3, posted.Lines.Count); // 2 expense lines + 1 float credit
        Assert.Equal(25_000, posted.Lines.Sum(l => l.DebitMinor));
        Assert.Equal(25_000, posted.Lines.Sum(l => l.CreditMinor));
    }

    // ------------------------------------------------------------------
    // Float invariants
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateFloat_RejectsSecondActiveFloatPerSite()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var site = Guid.NewGuid();
        await svc.CreateFloatAsync(site, Guid.NewGuid(), Ghs(100_000), Guid.NewGuid());

        await Assert.ThrowsAsync<PettyCashException>(() =>
            svc.CreateFloatAsync(site, Guid.NewGuid(), Ghs(100_000), Guid.NewGuid()));
    }

    [Fact]
    public async Task CreateFloat_RejectsNegativeInitial()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(-100), Guid.NewGuid()));
    }

    // ------------------------------------------------------------------
    // Submit shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task Submit_RejectsMismatchedLineTotal()
    {
        var (svc, fl) = await PreparedFloat();
        await Assert.ThrowsAsync<VoucherTotalMismatchException>(() =>
            svc.SubmitVoucherAsync(new SubmitVoucherRequest(
                fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "test",
                Ghs(20_000),
                new[] { new VoucherLineInput("a", Ghs(10_000)) })));
    }

    [Fact]
    public async Task Submit_RejectsMixedCurrency()
    {
        var (svc, fl) = await PreparedFloat();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SubmitVoucherAsync(new SubmitVoucherRequest(
                fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "test",
                Ghs(10_000),
                new[] { new VoucherLineInput("a", new Money(10_000, "USD")) })));
    }

    [Fact]
    public async Task Submit_RejectsZeroAmount()
    {
        var (svc, fl) = await PreparedFloat();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SubmitVoucherAsync(new SubmitVoucherRequest(
                fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "test",
                Ghs(0),
                new[] { new VoucherLineInput("a", Ghs(0)) })));
    }

    [Fact]
    public async Task Submit_RejectsAgainstClosedFloat()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(100_000), Guid.NewGuid());
        fl.IsActive = false;
        fl.ClosedAt = DateTimeOffset.UtcNow;
        fl.ClosedByUserId = Guid.NewGuid();
        await pc.SaveChangesAsync();

        await Assert.ThrowsAsync<FloatNotAvailableException>(() =>
            svc.SubmitVoucherAsync(new SubmitVoucherRequest(
                fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "test",
                Ghs(10_000),
                new[] { new VoucherLineInput("a", Ghs(10_000)) })));
    }

    // ------------------------------------------------------------------
    // Approve / reject — separation of duties + state guards
    // ------------------------------------------------------------------

    [Fact]
    public async Task Approve_RejectsRequesterApprovingTheirOwn()
    {
        var (svc, fl) = await PreparedFloat();
        var requester = Guid.NewGuid();
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, requester, VoucherCategory.Fuel, "fuel for the van",
            Ghs(30_000),
            new[] { new VoucherLineInput("V8 fuel", Ghs(30_000)) }));

        await Assert.ThrowsAsync<SeparationOfDutiesException>(() =>
            svc.ApproveVoucherAsync(v.VoucherId, requester, null, null));
    }

    /// <summary>
    /// Phase 1 of the role-overhaul wave (2026-04-29) added an explicit
    /// service-level submitter==approver guard to
    /// <see cref="PettyCashService.ApproveVoucherAsync"/>. The check sits
    /// at the top of the method so the SoD violation surfaces before the
    /// approval engine is consulted, regardless of which engine is
    /// configured. The thrown type is the existing
    /// <see cref="SeparationOfDutiesException"/> (consistent with the
    /// approver-vs-disburser check at the bottom of
    /// <c>DisburseVoucherAsync</c>).
    /// </summary>
    [Fact]
    public async Task ApproveVoucher_RejectsSelfApproval()
    {
        var (svc, fl) = await PreparedFloat();
        var sameUser = Guid.NewGuid();
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, sameUser, VoucherCategory.Transport, "self-approval guard",
            Ghs(12_345),
            new[] { new VoucherLineInput("ride share", Ghs(12_345)) }));

        var ex = await Assert.ThrowsAsync<SeparationOfDutiesException>(() =>
            svc.ApproveVoucherAsync(v.VoucherId, sameUser, null, "approving my own voucher"));
        Assert.Contains("submitter", ex.Message);
    }

    [Fact]
    public async Task Approve_RejectsAlreadyApproved()
    {
        var (svc, fl) = await PreparedFloat();
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Fuel, "fuel",
            Ghs(5_000),
            new[] { new VoucherLineInput("petrol", Ghs(5_000)) }));
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);

        await Assert.ThrowsAsync<InvalidVoucherTransitionException>(() =>
            svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null));
    }

    [Fact]
    public async Task Reject_RecordsReasonAndDecider()
    {
        var (svc, fl) = await PreparedFloat();
        var requester = Guid.NewGuid();
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, requester, VoucherCategory.OfficeSupplies, "stationery",
            Ghs(8_000),
            new[] { new VoucherLineInput("paper + pens", Ghs(8_000)) }));
        var approver = Guid.NewGuid();

        var rejected = await svc.RejectVoucherAsync(v.VoucherId, approver, "Use existing stock first.");

        Assert.Equal(VoucherStatus.Rejected, rejected.Status);
        Assert.Equal(approver, rejected.DecidedByUserId);
        Assert.Equal("Use existing stock first.", rejected.DecisionComment);
    }

    // ------------------------------------------------------------------
    // Disburse — separation of duties + atomicity
    // ------------------------------------------------------------------

    [Fact]
    public async Task Disburse_RejectsApproverActingAsCustodian()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var custodian = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), custodian, Ghs(100_000), Guid.NewGuid());

        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "x",
            Ghs(5_000),
            new[] { new VoucherLineInput("a", Ghs(5_000)) }));
        var approver = Guid.NewGuid();
        await svc.ApproveVoucherAsync(v.VoucherId, approver, null, null);

        await Assert.ThrowsAsync<SeparationOfDutiesException>(() =>
            svc.DisburseVoucherAsync(v.VoucherId, approver, new DateOnly(2026, 4, 1), period.PeriodId));
    }

    [Fact]
    public async Task Disburse_RejectedToClosedPeriod_LeavesVoucherApproved()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var periodSvc = new PeriodService(lg);
        // Use a unique year far from any other test so hard-closing here
        // doesn't poison the shared fixture DB for the rest of the run.
        var period = await periodSvc.CreateAsync(2099, 12);
        await periodSvc.HardCloseAsync(period.PeriodId, Guid.NewGuid());

        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(50_000), Guid.NewGuid());
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "x",
            Ghs(5_000),
            new[] { new VoucherLineInput("a", Ghs(5_000)) }));
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);

        await Assert.ThrowsAsync<ClosedPeriodException>(() =>
            svc.DisburseVoucherAsync(v.VoucherId, Guid.NewGuid(), new DateOnly(2026, 4, 1), period.PeriodId));

        // Voucher is still Approved — we can retry against an open period.
        await using var pc2 = _fx.CreatePettyCash();
        var stillApproved = await pc2.Vouchers.FirstAsync(x => x.VoucherId == v.VoucherId);
        Assert.Equal(VoucherStatus.Approved, stillApproved.Status);
        Assert.Null(stillApproved.LedgerEventId);
    }

    [Fact]
    public async Task Disburse_IsRetrySafe_ViaLedgerIdempotencyKey()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var writer = new LedgerWriter(lg);
        var svc = new PettyCashService(pc, writer);

        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(100_000), Guid.NewGuid());
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "x",
            Ghs(2_500),
            new[] { new VoucherLineInput("a", Ghs(2_500)) }));
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);

        var custodian = Guid.NewGuid();
        var first = await svc.DisburseVoucherAsync(v.VoucherId, custodian, new DateOnly(2026, 4, 10), period.PeriodId);

        // Re-build a fresh service against fresh contexts and replay
        // disburse. Because the voucher is now in Disbursed, a redo here
        // rightly throws — disburse is final. The Ledger's idempotency
        // makes the JOURNAL retry-safe internally; the voucher status
        // transition is what guards a second post.
        await using var pc2 = _fx.CreatePettyCash();
        await using var lg2 = _fx.CreateLedger();
        var svc2 = new PettyCashService(pc2, new LedgerWriter(lg2));
        await Assert.ThrowsAsync<InvalidVoucherTransitionException>(() =>
            svc2.DisburseVoucherAsync(v.VoucherId, custodian, new DateOnly(2026, 4, 10), period.PeriodId));

        // And the ledger only has one event for this voucher.
        var count = await lg2.Events.CountAsync(e => e.SourceEntityId == v.VoucherId.ToString("N"));
        Assert.Equal(1, count);
        Assert.Equal(first.LedgerEventId, await lg2.Events
            .Where(e => e.SourceEntityId == v.VoucherId.ToString("N"))
            .Select(e => e.EventId).FirstAsync());
    }

    [Fact]
    public async Task DefaultGlAccount_MapsEveryCategory()
    {
        // Sanity: every category maps to a valid (1000-9999) GL string.
        foreach (var cat in Enum.GetValues<VoucherCategory>())
        {
            var gl = cat.DefaultGlAccount();
            Assert.False(string.IsNullOrWhiteSpace(gl));
            Assert.Matches(@"^\d{4}$", gl);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Local shorthand: <c>Ghs(25_000)</c> = GHS 250.00 in minor units.</summary>
    private static Money Ghs(long minor) => new(minor, "GHS");

    private async Task<(PettyCashService svc, Float fl)> PreparedFloat()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(500_000), Guid.NewGuid());
        return (svc, fl);
    }
}
