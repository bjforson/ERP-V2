using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using NickFinance.PettyCash;
using NickFinance.TaxEngine;
using Xunit;

namespace NickFinance.PettyCash.Tests;

[Collection("PettyCash")]
public class TaxAwareDisbursementTests
{
    private static Money Ghs(long minor) => new(minor, "GHS");
    private readonly PettyCashFixture _fx;
    public TaxAwareDisbursementTests(PettyCashFixture fx) => _fx = fx;

    [Fact]
    public async Task GhanaInclusive_SplitsLevyAndVatLines_OnDisburse()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var requester = Guid.NewGuid();
        var approver = Guid.NewGuid();
        var custodian = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), custodian, Ghs(1_000_00), Guid.NewGuid());

        // GHS 121.90 inclusive of levies + VAT (the canonical "GHS 100 net" example).
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, requester, VoucherCategory.OfficeSupplies, "Stationery (VAT inv)",
            Ghs(121_90),
            new[] { new VoucherLineInput("Reams of A4", Ghs(121_90)) }));

        // Mark as Ghana-inclusive BEFORE submit-time persisted? The submit just
        // saved the row with default TaxTreatment.None — flip it before approval.
        v.TaxTreatment = TaxTreatment.GhanaInclusive;
        await pc.SaveChangesAsync();

        await svc.ApproveVoucherAsync(v.VoucherId, approver, null, "OK");
        v = await svc.DisburseVoucherAsync(v.VoucherId, custodian, new DateOnly(2026, 4, 10), period.PeriodId);
        Assert.Equal(VoucherStatus.Disbursed, v.Status);

        // Inspect the journal: should have one line per (net + each levy + VAT) + one credit.
        await using var lg2 = _fx.CreateLedger();
        var journal = await lg2.Events
            .Include(e => e.Lines)
            .FirstAsync(e => e.EventId == v.LedgerEventId);

        var byCode = journal.Lines.GroupBy(l => l.AccountCode)
            .ToDictionary(g => g.Key, g => (Dr: g.Sum(l => l.DebitMinor), Cr: g.Sum(l => l.CreditMinor)));

        // 6410 Office supplies — DR net (10000) + DR all three levy lines (250+250+100) = 10600
        Assert.Equal(10_000 + 250 + 250 + 100, byCode["6410"].Dr);
        // 1410 VAT input recoverable — DR 1590 (15.9% of 10600 levy-inclusive base)
        Assert.Equal(15_90, byCode["1410"].Dr);
        // 1060 Petty cash float — CR full gross
        Assert.Equal(121_90, byCode["1060"].Cr);
        // Sums match
        Assert.Equal(journal.Lines.Sum(l => l.DebitMinor), journal.Lines.Sum(l => l.CreditMinor));
    }

    [Fact]
    public async Task WhtServices_SplitsCreditBetweenFloatAndWht()
    {
        await using var pc = _fx.CreatePettyCash();
        await using var lg = _fx.CreateLedger();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new PettyCashService(pc, new LedgerWriter(lg));

        var custodian = Guid.NewGuid();
        var fl = await svc.CreateFloatAsync(Guid.NewGuid(), custodian, Ghs(10_000_00), Guid.NewGuid());
        // GHS 1000 service fee — 7.5% WHT = GHS 75 to GRA, GHS 925 to supplier.
        var v = await svc.SubmitVoucherAsync(new SubmitVoucherRequest(
            fl.FloatId, Guid.NewGuid(), VoucherCategory.Emergency, "Plumber callout",
            Ghs(1_000_00),
            new[] { new VoucherLineInput("Bathroom plumbing", Ghs(1_000_00)) }));
        v.WhtTreatment = WhtTreatment.Services;
        await pc.SaveChangesAsync();
        await svc.ApproveVoucherAsync(v.VoucherId, Guid.NewGuid(), null, null);
        v = await svc.DisburseVoucherAsync(v.VoucherId, custodian, new DateOnly(2026, 4, 11), period.PeriodId);

        await using var lg2 = _fx.CreateLedger();
        var journal = await lg2.Events.Include(e => e.Lines).FirstAsync(e => e.EventId == v.LedgerEventId);
        var byCode = journal.Lines.GroupBy(l => l.AccountCode)
            .ToDictionary(g => g.Key, g => (Dr: g.Sum(l => l.DebitMinor), Cr: g.Sum(l => l.CreditMinor)));

        Assert.Equal(1_000_00, byCode["6400"].Dr);  // category default GL for Emergency is 6400
        Assert.Equal(75_00, byCode["2150"].Cr);     // WHT payable
        Assert.Equal(925_00, byCode["1060"].Cr);    // float drawdown net of WHT
        // Balanced
        Assert.Equal(journal.Lines.Sum(l => l.DebitMinor), journal.Lines.Sum(l => l.CreditMinor));
    }
}
