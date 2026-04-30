using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NickFinance.AP;
using NickFinance.Ledger;
using NickFinance.TaxEngine;
using Npgsql;
using Xunit;

namespace NickFinance.AP.Tests;

public sealed class ApFixture : IAsyncLifetime
{
    public string ConnectionString { get; }
    public ApFixture()
    {
        var raw = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException("NICKFINANCE_TEST_DB env var required.");
        ConnectionString = Rewrite(raw, "nickscan_ap_test");
    }
    public LedgerDbContext NewLedger() => new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);
    public ApDbContext NewAp() => new(new DbContextOptionsBuilder<ApDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var lg = NewLedger())
        {
            await lg.Database.EnsureDeletedAsync();
            await lg.Database.EnsureCreatedAsync();
            await SchemaBootstrap.ApplyConstraintsAsync(lg);
        }
        await using (var ap = NewAp())
        {
            var creator = (IRelationalDatabaseCreator)ap.Database.GetService<IDatabaseCreator>();
            await creator.CreateTablesAsync();
        }
    }
    public async Task DisposeAsync()
    {
        try { NpgsqlConnection.ClearAllPools(); await using var lg = NewLedger(); await lg.Database.EnsureDeletedAsync(); }
        catch { }
    }
    private static string Rewrite(string c, string n)
    {
        var p = c.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < p.Length; i++)
            if (p[i].StartsWith("Database=", StringComparison.OrdinalIgnoreCase)) p[i] = $"Database={n}";
        return string.Join(';', p);
    }
}

[CollectionDefinition("AP")] public class ApCollection : ICollectionFixture<ApFixture> { }

[Collection("AP")]
public class ApServiceTests
{
    private readonly ApFixture _fx;
    public ApServiceTests(ApFixture fx) => _fx = fx;

    [Fact]
    public async Task EndToEnd_Vendor_Capture_Approve_Pay_WithWht_AndCertificate()
    {
        await using var lg = _fx.NewLedger();
        await using var ap = _fx.NewAp();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new ApService(ap, new LedgerWriter(lg));

        var vendor = await svc.UpsertVendorAsync(new UpsertVendorRequest(
            "ACME-001", "Acme Plumbing Ltd", Tin: "P0099887766", IsVatRegistered: true,
            DefaultWht: WhtTransactionType.SupplyOfServices));

        // Pre-tax bill — line is GHS 1,000 net; AP adds 60 levies + 159 VAT = 219 tax, gross 1,219.
        var bill = await svc.CaptureBillAsync(new CaptureBillRequest(
            vendor.VendorId, "ACME-INV-7777", new DateOnly(2026, 4, 5), new DateOnly(2026, 5, 5),
            BillTaxTreatment.PreTax,
            new[] { new CaptureBillLine("Plumbing service", 1_000_00) },
            CurrencyCode: "GHS"));
        Assert.Equal(ApBillStatus.Captured, bill.Status);
        Assert.Equal(1_000_00, bill.SubtotalNetMinor);
        Assert.Equal(60_00, bill.LeviesMinor);
        Assert.Equal(159_00, bill.VatMinor);
        Assert.Equal(1_219_00, bill.GrossMinor);
        Assert.StartsWith("AP-2026-04-", bill.BillNo);

        bill = await svc.ApproveBillAsync(bill.ApBillId, Guid.NewGuid(), new DateOnly(2026, 4, 5), period.PeriodId);
        Assert.Equal(ApBillStatus.Approved, bill.Status);
        Assert.NotNull(bill.LedgerEventId);

        // Pay the full gross — 7.5% WHT applies = 91.43 to GRA, supplier nets 1127.57.
        var (payment, cert) = await svc.PayBillAsync(new PayBillRequest(
            bill.ApBillId, new DateOnly(2026, 4, 12),
            AmountMinor: 1_219_00,
            PaymentRail: "bank",
            CashAccount: "1030",
            PaymentRunId: Guid.NewGuid(),
            RecordedByUserId: Guid.NewGuid(),
            RailReference: "GCB-T-9988"), period.PeriodId);

        Assert.Equal(1_219_00, payment.AmountMinor);
        Assert.NotNull(cert);
        Assert.Equal(0.075m, cert!.WhtRate);
        // 7.5% of GHS 1219.00 = 91.425. Banker's rounding to even → 91.42 (=9142 minor).
        var expectedWht = 9142L;
        var expectedNet = 1_219_00 - expectedWht;
        Assert.Equal(expectedWht, cert.WhtDeductedMinor);
        Assert.StartsWith("WHT-2026-04-", cert.CertificateNo);

        await using var ap2 = _fx.NewAp();
        var paid = await ap2.Bills.FirstAsync(b => b.ApBillId == bill.ApBillId);
        Assert.Equal(ApBillStatus.Paid, paid.Status);
        Assert.Equal(expectedWht, paid.WhtMinor);

        // Journal sanity: DR 2000 / CR 2150 + CR 1030 (sum CR == 1219_00).
        await using var lg2 = _fx.NewLedger();
        var journal = await lg2.Events.Include(e => e.Lines).FirstAsync(e => e.EventId == payment.LedgerEventId);
        var byCode = journal.Lines.GroupBy(l => l.AccountCode)
            .ToDictionary(g => g.Key, g => (Dr: g.Sum(l => l.DebitMinor), Cr: g.Sum(l => l.CreditMinor)));
        Assert.Equal(1_219_00, byCode["2000"].Dr);
        Assert.Equal(expectedWht, byCode["2150"].Cr);
        Assert.Equal(expectedNet, byCode["1030"].Cr);
        Assert.Equal(journal.Lines.Sum(l => l.DebitMinor), journal.Lines.Sum(l => l.CreditMinor));
    }

    [Fact]
    public async Task GhanaInclusive_Bill_BackSolvesNet()
    {
        await using var lg = _fx.NewLedger();
        await using var ap = _fx.NewAp();
        var svc = new ApService(ap, new LedgerWriter(lg));

        var v = await svc.UpsertVendorAsync(new UpsertVendorRequest("INC-001", "Inclusive Test"));
        var bill = await svc.CaptureBillAsync(new CaptureBillRequest(
            v.VendorId, "INV-INC-1", new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1),
            BillTaxTreatment.GhanaInclusive,
            new[] { new CaptureBillLine("All-in receipt", 121_90) }));
        // Reverse of FromGross(12190): net 100.00, levies 6.00, VAT 15.90.
        Assert.Equal(100_00, bill.SubtotalNetMinor);
        Assert.Equal(6_00, bill.LeviesMinor);
        Assert.Equal(15_90, bill.VatMinor);
        Assert.Equal(121_90, bill.GrossMinor);
        Assert.Equal(bill.SubtotalNetMinor + bill.LeviesMinor + bill.VatMinor, bill.GrossMinor);
    }

    [Fact]
    public async Task Aging_BucketsByDueDate()
    {
        await using var lg = _fx.NewLedger();
        await using var ap = _fx.NewAp();
        const long Tenant = 7200;
        var period = await new PeriodService(lg).CreateAsync(2026, 1, Tenant);
        var svc = new ApService(ap, new LedgerWriter(lg));
        var v = await svc.UpsertVendorAsync(new UpsertVendorRequest("AGE-AP", "Aging vendor", TenantId: Tenant));

        DateOnly[] dueDates = { new(2026, 4, 25), new(2026, 3, 1), new(2026, 1, 15) };
        foreach (var d in dueDates)
        {
            var b = await svc.CaptureBillAsync(new CaptureBillRequest(
                v.VendorId, $"REF-{d:O}", d.AddDays(-30), d,
                BillTaxTreatment.None,
                new[] { new CaptureBillLine("svc", 100_00) },
                TenantId: Tenant));
            await svc.ApproveBillAsync(b.ApBillId, Guid.NewGuid(), d.AddDays(-30), period.PeriodId);
        }
        var aging = await svc.AgingReportAsync(new DateOnly(2026, 4, 30), Tenant);
        Assert.Equal(4, aging.Count);
        Assert.Equal(1, aging[0].BillCount);   // 0-30
        Assert.Equal(1, aging[1].BillCount);   // 31-60
        Assert.Equal(0, aging[2].BillCount);   // 61-90
        Assert.Equal(1, aging[3].BillCount);   // 90+
    }
}
