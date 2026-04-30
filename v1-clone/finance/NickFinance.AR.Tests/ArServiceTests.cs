using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NickFinance.AR;
using NickFinance.Ledger;
using Npgsql;
using Xunit;

namespace NickFinance.AR.Tests;

public sealed class ArFixture : IAsyncLifetime
{
    public string ConnectionString { get; }

    public ArFixture()
    {
        var raw = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException("NICKFINANCE_TEST_DB env var required.");
        ConnectionString = RewriteDb(raw, "nickscan_ar_test");
    }

    public LedgerDbContext NewLedger() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);

    public ArDbContext NewAr() =>
        new(new DbContextOptionsBuilder<ArDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var lg = NewLedger())
        {
            await lg.Database.EnsureDeletedAsync();
            await lg.Database.EnsureCreatedAsync();
            await SchemaBootstrap.ApplyConstraintsAsync(lg);
        }
        await using (var ar = NewAr())
        {
            var creator = (IRelationalDatabaseCreator)ar.Database.GetService<IDatabaseCreator>();
            await creator.CreateTablesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var lg = NewLedger();
            await lg.Database.EnsureDeletedAsync();
        }
        catch { }
    }

    private static string RewriteDb(string c, string n)
    {
        var p = c.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < p.Length; i++)
            if (p[i].StartsWith("Database=", StringComparison.OrdinalIgnoreCase)) p[i] = $"Database={n}";
        return string.Join(';', p);
    }
}

[CollectionDefinition("AR")]
public class ArCollection : ICollectionFixture<ArFixture> { }

[Collection("AR")]
public class ArServiceTests
{
    private readonly ArFixture _fx;
    public ArServiceTests(ArFixture fx) => _fx = fx;

    [Fact]
    public async Task EndToEnd_CreateCustomer_DraftIssue_Receipt_FullyPaid()
    {
        await using var lg = _fx.NewLedger();
        await using var ar = _fx.NewAr();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new ArService(ar, new LedgerWriter(lg));

        var cust = await svc.CreateCustomerAsync(new CreateCustomerRequest(
            "AGRO-001", "Agro Importers Ltd", Tin: "P0012345678", IsVatRegistered: true));

        // GHS 1,000 net invoice — Ghana taxes will land 60 levies + 159 VAT = 219 tax, gross 1,219.
        var inv = await svc.DraftInvoiceAsync(new DraftInvoiceRequest(
            cust.CustomerId, new DateOnly(2026, 4, 5), new DateOnly(2026, 5, 5),
            Reference: "P/O 4477",
            Lines: new[] { new DraftInvoiceLine("Customs scan service", 1_000_00) }));
        Assert.Equal(ArInvoiceStatus.Draft, inv.Status);

        inv = await svc.IssueInvoiceAsync(inv.ArInvoiceId, Guid.NewGuid(), new DateOnly(2026, 4, 5), period.PeriodId);
        Assert.Equal(ArInvoiceStatus.Issued, inv.Status);
        Assert.Equal(1_000_00, inv.SubtotalNetMinor);
        Assert.Equal(60_00, inv.LeviesMinor);   // 6% of 1000
        Assert.Equal(159_00, inv.VatMinor);     // 15% of 1060
        Assert.Equal(1_219_00, inv.GrossMinor);
        Assert.NotNull(inv.EvatIrn);
        Assert.True(StubEvatProvider.IsSandbox(inv.EvatIrn),
            $"Expected sandbox-prefixed IRN, got '{inv.EvatIrn}'.");
        Assert.NotNull(inv.LedgerEventId);
        Assert.StartsWith("INV-2026-04-", inv.InvoiceNo);

        // Inspect the journal — 1100 debit gross, splits per line + tax.
        await using var lg2 = _fx.NewLedger();
        var journal = await lg2.Events.Include(e => e.Lines).FirstAsync(e => e.EventId == inv.LedgerEventId);
        var byCode = journal.Lines.GroupBy(l => l.AccountCode)
            .ToDictionary(g => g.Key, g => (Dr: g.Sum(l => l.DebitMinor), Cr: g.Sum(l => l.CreditMinor)));
        Assert.Equal(1_219_00, byCode["1100"].Dr);
        Assert.Equal(1_000_00, byCode["4010"].Cr);
        Assert.Equal(159_00, byCode["2110"].Cr);
        Assert.Equal(25_00, byCode["2120"].Cr);
        Assert.Equal(25_00, byCode["2130"].Cr);
        Assert.Equal(10_00, byCode["2140"].Cr);

        // Partial receipt
        var r1 = await svc.RecordReceiptAsync(new RecordReceiptRequest(
            inv.ArInvoiceId, new DateOnly(2026, 4, 12), 500_00,
            CashAccount: "1030", RecordedByUserId: Guid.NewGuid(),
            Reference: "GCB MOMO 4488"), period.PeriodId);
        Assert.Equal(500_00, r1.AmountMinor);

        await using var ar2 = _fx.NewAr();
        var midPay = await ar2.Invoices.FirstAsync(i => i.ArInvoiceId == inv.ArInvoiceId);
        Assert.Equal(ArInvoiceStatus.PartiallyPaid, midPay.Status);
        Assert.Equal(719_00, midPay.OutstandingMinor);

        // Final receipt closes the invoice.
        var r2 = await svc.RecordReceiptAsync(new RecordReceiptRequest(
            midPay.ArInvoiceId, new DateOnly(2026, 4, 20), 719_00,
            CashAccount: "1030", RecordedByUserId: Guid.NewGuid()), period.PeriodId);
        Assert.Equal(719_00, r2.AmountMinor);

        await using var ar3 = _fx.NewAr();
        var finalInv = await ar3.Invoices.FirstAsync(i => i.ArInvoiceId == inv.ArInvoiceId);
        Assert.Equal(ArInvoiceStatus.Paid, finalInv.Status);
        Assert.Equal(0, finalInv.OutstandingMinor);
    }

    [Fact]
    public async Task ScanCompleted_AutoDraftsInvoice_AndIsIdempotent()
    {
        await using var lg = _fx.NewLedger();
        await using var ar = _fx.NewAr();
        var svc = new ArService(ar, new LedgerWriter(lg));

        var cust = await svc.CreateCustomerAsync(new CreateCustomerRequest("KAMP-001", "Kampong Trading"));
        var ev = new ScanCompletedEvent("DECL-2026-04-9999", cust.CustomerId, 250_00, "GHS", new DateOnly(2026, 4, 8));

        var inv1 = await svc.ScanCompletedAsync(ev);
        Assert.Equal(ArInvoiceStatus.Draft, inv1.Status);
        Assert.Equal("scan_to_invoice", inv1.SourceModule);
        Assert.Equal("DECL-2026-04-9999", inv1.SourceEntityId);

        // Re-fire the same event — should NOT create a second invoice.
        var inv2 = await svc.ScanCompletedAsync(ev);
        Assert.Equal(inv1.ArInvoiceId, inv2.ArInvoiceId);

        await using var ar2 = _fx.NewAr();
        var count = await ar2.Invoices.CountAsync(i => i.SourceEntityId == ev.DeclarationNumber);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Aging_BucketsByDaysOverdue()
    {
        await using var lg = _fx.NewLedger();
        await using var ar = _fx.NewAr();
        const long Tenant = 7100;
        var period = await new PeriodService(lg).CreateAsync(2026, 1, Tenant);
        var svc = new ArService(ar, new LedgerWriter(lg));
        var cust = await svc.CreateCustomerAsync(new CreateCustomerRequest("AGE-001", "Aging Test", TenantId: Tenant));

        // Three invoices issued, due in different windows from "asOf" 2026-04-30.
        // Due 2026-04-25 (5 days overdue → 0-30)
        // Due 2026-03-01 (60 days     → 31-60? No — 60 inclusive in 31-60)
        // Due 2026-01-15 (105 days    → 90+)
        DateOnly[] dueDates = { new(2026, 4, 25), new(2026, 3, 1), new(2026, 1, 15) };
        foreach (var due in dueDates)
        {
            var inv = await svc.DraftInvoiceAsync(new DraftInvoiceRequest(
                cust.CustomerId, due.AddDays(-30), due,
                Reference: $"due {due:O}",
                Lines: new[] { new DraftInvoiceLine("svc", 100_00) },
                TenantId: Tenant));
            await svc.IssueInvoiceAsync(inv.ArInvoiceId, Guid.NewGuid(), due.AddDays(-30), period.PeriodId);
        }

        var aging = await svc.AgingReportAsync(new DateOnly(2026, 4, 30), Tenant);
        Assert.Equal(4, aging.Count);
        Assert.Equal("0-30", aging[0].Bucket); Assert.Equal(1, aging[0].InvoiceCount);
        Assert.Equal("31-60", aging[1].Bucket); Assert.Equal(1, aging[1].InvoiceCount);
        Assert.Equal("61-90", aging[2].Bucket); Assert.Equal(0, aging[2].InvoiceCount);
        Assert.Equal("90+", aging[3].Bucket); Assert.Equal(1, aging[3].InvoiceCount);
    }
}
