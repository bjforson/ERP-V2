using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NickFinance.AP;
using NickFinance.Banking;
using NickFinance.Ledger;
using NickFinance.TaxEngine;
using Npgsql;
using Xunit;

namespace NickFinance.Banking.Tests;

public sealed class BankingFixture : IAsyncLifetime
{
    public string ConnectionString { get; }
    public BankingFixture()
    {
        var raw = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException("NICKFINANCE_TEST_DB env var required.");
        ConnectionString = Rewrite(raw, "nickscan_banking_test");
    }
    public LedgerDbContext NewLedger() => new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);
    public BankingDbContext NewBanking() => new(new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(ConnectionString).Options);
    public ApDbContext NewAp() => new(new DbContextOptionsBuilder<ApDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var lg = NewLedger())
        {
            await lg.Database.EnsureDeletedAsync();
            await lg.Database.EnsureCreatedAsync();
            await SchemaBootstrap.ApplyConstraintsAsync(lg);
        }
        await using (var bk = NewBanking())
        {
            var creator = (IRelationalDatabaseCreator)bk.Database.GetService<IDatabaseCreator>();
            await creator.CreateTablesAsync();
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

[CollectionDefinition("Banking")] public class BankingCollection : ICollectionFixture<BankingFixture> { }

[Collection("Banking")]
public class BankingServiceTests
{
    private readonly BankingFixture _fx;
    public BankingServiceTests(BankingFixture fx) => _fx = fx;

    [Fact]
    public async Task Generic_CsvParser_RoundTripsAStatement()
    {
        var csv = """
        Date,Description,Reference,Debit,Credit
        2026-04-01,Opening,,0,0
        2026-04-02,Plumber,REF-AP-1,1219.00,
        2026-04-05,Customer wire,SCAN-INV-1,,1500.00
        """;
        var parser = new GenericBankCsvParser();
        var parsed = await parser.ParseAsync(Encoding.UTF8.GetBytes(csv), "GHS");
        Assert.Equal(2, parsed.Rows.Count);
        var debit = parsed.Rows.Single(r => r.Direction == BankTransactionDirection.Debit && r.AmountMinor == 1_219_00);
        Assert.Equal("REF-AP-1", debit.Reference);
        Assert.Equal(new DateOnly(2026, 4, 2), debit.TransactionDate);
        var credit = parsed.Rows.Single(r => r.Direction == BankTransactionDirection.Credit && r.AmountMinor == 1_500_00);
        Assert.Equal("SCAN-INV-1", credit.Reference);
    }

    [Fact]
    public async Task ImportAndAutoMatch_AgainstApPayment()
    {
        await using var lg = _fx.NewLedger();
        await using var bk = _fx.NewBanking();
        await using var ap = _fx.NewAp();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);

        // Set up an AP payment that should auto-match.
        var apSvc = new ApService(ap, new LedgerWriter(lg));
        var vendor = await apSvc.UpsertVendorAsync(new UpsertVendorRequest("BANK-V1", "Bank Test Vendor", DefaultWht: WhtTransactionType.Exempt));
        var bill = await apSvc.CaptureBillAsync(new CaptureBillRequest(
            vendor.VendorId, "BTV-INV-1", new DateOnly(2026, 4, 5), new DateOnly(2026, 5, 5),
            BillTaxTreatment.None,
            new[] { new CaptureBillLine("Service", 500_00) }));
        await apSvc.ApproveBillAsync(bill.ApBillId, Guid.NewGuid(), new DateOnly(2026, 4, 5), period.PeriodId);
        await apSvc.PayBillAsync(new PayBillRequest(
            bill.ApBillId, new DateOnly(2026, 4, 6), 500_00, "bank", "1030",
            Guid.NewGuid(), Guid.NewGuid()), period.PeriodId);

        // Import a statement.
        var bankSvc = new BankingService(bk, new BankCsvParserRegistry(), new LedgerReader(lg), ap);
        var account = await bankSvc.UpsertAccountAsync(new UpsertBankAccountRequest(
            "GCB-1", "GCB Operations", "GCB", "1234567890", "1030"));
        var csv = "Date,Description,Reference,Debit,Credit\n2026-04-06,Vendor pay,BTV-PAY,500.00,\n";
        var stmt = await bankSvc.ImportStatementAsync(new ImportStatementRequest(
            account.BankAccountId, new DateOnly(2026, 4, 6), "april.csv", "generic",
            Encoding.UTF8.GetBytes(csv), Guid.NewGuid()));
        Assert.NotNull(stmt);

        var matched = await bankSvc.AutoMatchAsync(account.BankAccountId, new MatchTolerance(DateWindowDays: 2, AmountMinorTolerance: 0));
        Assert.Equal(1, matched);

        var rows = await bk.Transactions.Where(t => t.BankAccountId == account.BankAccountId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(BankMatchStatus.Provisional, rows[0].MatchStatus);
        Assert.Equal("ApPayment", rows[0].MatchedToEntityType);
    }

    [Fact]
    public async Task OpenAndClose_ReconciliationSession()
    {
        await using var lg = _fx.NewLedger();
        await using var bk = _fx.NewBanking();
        await using var ap = _fx.NewAp();
        var bankSvc = new BankingService(bk, new BankCsvParserRegistry(), new LedgerReader(lg), ap);
        var account = await bankSvc.UpsertAccountAsync(new UpsertBankAccountRequest(
            "GCB-2", "GCB Recon", "GCB", "9876543210", "1030"));

        var sess = await bankSvc.OpenReconciliationAsync(account.BankAccountId,
            new DateOnly(2026, 4, 30), bankBalanceMinor: 12_345_67, actorUserId: Guid.NewGuid());
        Assert.Equal(ReconciliationStatus.Open, sess.Status);
        Assert.Equal(12_345_67, sess.BankBalanceMinor);

        var closed = await bankSvc.CloseReconciliationAsync(sess.ReconciliationSessionId, Guid.NewGuid(), "matched out");
        Assert.Equal(ReconciliationStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAt);
    }
}
