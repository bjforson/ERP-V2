using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using NickFinance.PettyCash;
using NickFinance.PettyCash.Budgets;
using NickFinance.PettyCash.CashCounts;
using NickFinance.PettyCash.Fraud;
using Xunit;

namespace NickFinance.PettyCash.Tests;

[Collection("PettyCash")]
public class OperationsTests
{
    private static Money Ghs(long minor) => new(minor, "GHS");
    private readonly PettyCashFixture _fx;
    public OperationsTests(PettyCashFixture fx) => _fx = fx;

    // -----------------------------------------------------------------
    // Cash counts
    // -----------------------------------------------------------------

    [Fact]
    public async Task CashCount_ZeroVariance_PersistsCleanly()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        // Unique tenant so other tests' 1060 postings don't bleed into our
        // "system amount" reading and break the zero-variance assumption.
        const long Tenant = 9001;
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(1_000_00), Guid.NewGuid(), Tenant);

        var counter = new CashCountService(pc, new LedgerReader(lg));
        var c = await counter.RecordAsync(fl.FloatId,
            physicalAmountMinor: 0,
            currencyCode: "GHS",
            countedByUserId: Guid.NewGuid(),
            witnessUserId: Guid.NewGuid(),
            varianceReason: null,
            asOfDate: new DateOnly(2026, 4, 20),
            tenantId: Tenant);
        Assert.Equal(0, c.VarianceMinor);

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    [Fact]
    public async Task CashCount_NonZeroVariance_RequiresReason()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(1_000_00), Guid.NewGuid());

        var counter = new CashCountService(pc, new LedgerReader(lg));
        await Assert.ThrowsAsync<PettyCashException>(() =>
            counter.RecordAsync(fl.FloatId,
                physicalAmountMinor: 50_00,            // GHS 50 in the box, system says 0 → variance +5000
                currencyCode: "GHS",
                countedByUserId: Guid.NewGuid(),
                witnessUserId: null,
                varianceReason: null,
                asOfDate: new DateOnly(2026, 4, 20)));

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    [Fact]
    public async Task CashCount_RejectsWitnessSameAsCounter()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(1_000_00), Guid.NewGuid());
        var counter = new CashCountService(pc, new LedgerReader(lg));
        var u = Guid.NewGuid();

        await Assert.ThrowsAsync<SeparationOfDutiesException>(() =>
            counter.RecordAsync(fl.FloatId, 0, "GHS", u, u, null, new DateOnly(2026, 4, 20)));

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    // -----------------------------------------------------------------
    // Budgets
    // -----------------------------------------------------------------

    [Fact]
    public async Task Budget_ConsumedIncrementsOnDisburse()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var siteId = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(siteId, Guid.NewGuid(), Ghs(10_000_00), Guid.NewGuid());

        var budgets = new BudgetService(pc);
        var siteCap = await budgets.CreateAsync(BudgetScope.Site, siteId.ToString("N"),
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30),
            amountMinor: 5_000_00, currencyCode: "GHS",
            actorUserId: Guid.NewGuid(), alertThresholdPct: 80);

        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "x",
            Ghs(100_00), new[] { new VoucherLineInput("a", Ghs(100_00)) }));
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);
        v = await svc.DisburseVoucherAsync(v.VoucherId, Guid.NewGuid(), new DateOnly(2026, 4, 11), period.PeriodId);

        var touched = await budgets.ApplyVoucherAsync(v, siteId);
        Assert.Single(touched);
        Assert.Equal(100_00, touched[0].ConsumedMinor);

        var alerts = await budgets.CheckAlertsAsync(touched);
        Assert.Equal(AlertSeverity.Ok, alerts[0].Severity);   // 100/5000 = 2% — well under threshold

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    [Fact]
    public async Task Budget_AlertsOnThresholdAndExceeded()
    {
        var pc = _fx.CreatePettyCash();
        var budgets = new BudgetService(pc);

        var b = await budgets.CreateAsync(BudgetScope.Category, "1",
            new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30),
            amountMinor: 1_000_00, currencyCode: "GHS",
            actorUserId: Guid.NewGuid(), alertThresholdPct: 80);

        // Manually advance consumed to test the alert classifier without
        // walking a full Disburse pipeline.
        b.ConsumedMinor = 850_00;     // 85% — Warning
        var alertsWarn = await budgets.CheckAlertsAsync(new[] { b });
        Assert.Equal(AlertSeverity.Warning, alertsWarn[0].Severity);

        b.ConsumedMinor = 1_500_00;   // 150% — Exceeded
        var alertsExceeded = await budgets.CheckAlertsAsync(new[] { b });
        Assert.Equal(AlertSeverity.Exceeded, alertsExceeded[0].Severity);

        await pc.DisposeAsync();
    }

    // -----------------------------------------------------------------
    // Fraud detector
    // -----------------------------------------------------------------

    [Fact]
    public async Task Fraud_F1_SalamiSlicing()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        const long Tenant = 9101;

        // Same requester, three vouchers across DIFFERENT sites so each gets
        // a unique voucher_no prefix (the count-then-format generator only
        // partitions by site within a year). The detector only cares about
        // requester+day, not site.
        var requester = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(10_000_00), Guid.NewGuid(), Tenant);
            var amount = Ghs(19_000);   // 95% of 20K threshold
            var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
                fl.FloatId, requester, VoucherCategory.Transport, $"trip {i}",
                amount, new[] { new VoucherLineInput($"a{i}", amount) },
                TenantId: Tenant));
            // Force same-day timestamp so the salami detector groups them.
            v.CreatedAt = new DateTimeOffset(2099, 6, 1, 9, 0, 0, TimeSpan.Zero);
        }
        await pc.SaveChangesAsync();

        var detector = new FraudDetector(pc);
        var signals = await detector.ScanAsync(new DateOnly(2099, 6, 1), new DateOnly(2099, 6, 1), Tenant);
        Assert.Contains(signals, s => s.Code == "F1_SALAMI" && s.RequesterUserId == requester);

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }

    [Fact]
    public async Task Fraud_F7_AfterHoursSubmission()
    {
        var pc = _fx.CreatePettyCash();
        var lg = _fx.CreateLedger();
        var svc = new PettyCashService(pc, new LedgerWriter(lg));
        const long Tenant = 9102;
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), Guid.NewGuid(), Ghs(10_000_00), Guid.NewGuid(), Tenant);

        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Transport, "midnight cab",
            Ghs(5_000), new[] { new VoucherLineInput("a", Ghs(5_000)) },
            TenantId: Tenant));
        // Override SubmittedAt to 02:30 UTC + scope to a unique year so the
        // detector picks just our voucher.
        v.CreatedAt = new DateTimeOffset(2099, 7, 1, 2, 30, 0, TimeSpan.Zero);
        v.SubmittedAt = v.CreatedAt;
        await pc.SaveChangesAsync();

        var detector = new FraudDetector(pc);
        var signals = await detector.ScanAsync(new DateOnly(2099, 7, 1), new DateOnly(2099, 7, 1), Tenant);
        Assert.Contains(signals, s => s.Code == "F7_AFTER_HOURS_SUBMIT" && s.VoucherId == v.VoucherId);

        await pc.DisposeAsync(); await lg.DisposeAsync();
    }
}
