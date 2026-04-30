using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NickFinance.Coa;
using NickFinance.Ledger;
using NickFinance.Reporting;
using Npgsql;
using Xunit;

namespace NickFinance.Reporting.Tests;

public sealed class ReportsFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    public ReportsFixture()
    {
        var raw = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException("NICKFINANCE_TEST_DB env var required.");
        ConnectionString = RewriteDb(raw, "nickscan_reporting_test");
    }

    public LedgerDbContext NewLedger() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);

    public CoaDbContext NewCoa() =>
        new(new DbContextOptionsBuilder<CoaDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var lg = NewLedger())
        {
            await lg.Database.EnsureDeletedAsync();
            await lg.Database.EnsureCreatedAsync();
            await SchemaBootstrap.ApplyConstraintsAsync(lg);
        }
        await using (var coa = NewCoa())
        {
            var creator = (IRelationalDatabaseCreator)coa.Database.GetService<IDatabaseCreator>();
            await creator.CreateTablesAsync();
            // Seed the standard chart so reports can attach Type / Name.
            var svc = new CoaService(coa);
            await svc.SeedGhanaStandardChartAsync(tenantId: 1);
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

    private static string RewriteDb(string conn, string newDb)
    {
        var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("Database=", StringComparison.OrdinalIgnoreCase))
                parts[i] = $"Database={newDb}";
        }
        return string.Join(';', parts);
    }
}

[CollectionDefinition("Reports")]
public class ReportsCollection : ICollectionFixture<ReportsFixture> { }

[Collection("Reports")]
public class FinancialReportsTests
{
    private static Money Ghs(long minor) => new(minor, "GHS");
    private readonly ReportsFixture _fx;
    public FinancialReportsTests(ReportsFixture fx) => _fx = fx;

    [Fact]
    public async Task TrialBalance_Balances_AfterMixedJournals()
    {
        await using var lg = _fx.NewLedger();
        await using var coa = _fx.NewCoa();
        const long Tenant = 8001;
        await new CoaService(coa).SeedGhanaStandardChartAsync(Tenant);
        var period = await new PeriodService(lg).CreateAsync(2026, 4, Tenant);
        var writer = new LedgerWriter(lg);

        // Two simple journals — pay rent and recognise revenue.
        await writer.PostAsync(new LedgerEvent
        {
            TenantId = Tenant,
            EffectiveDate = new DateOnly(2026, 4, 5),
            PeriodId = period.PeriodId,
            SourceModule = "test", SourceEntityType = "rent", SourceEntityId = "1",
            IdempotencyKey = "rep-tb-1",
            Narration = "Rent paid",
            ActorUserId = Guid.NewGuid(),
            Lines =
            {
                new LedgerEventLine { AccountCode = "6100", DebitMinor  = 5_000_00, CurrencyCode = "GHS" },
                new LedgerEventLine { AccountCode = "1030", CreditMinor = 5_000_00, CurrencyCode = "GHS" }
            }
        });
        await writer.PostAsync(new LedgerEvent
        {
            TenantId = Tenant,
            EffectiveDate = new DateOnly(2026, 4, 12),
            PeriodId = period.PeriodId,
            SourceModule = "test", SourceEntityType = "rev", SourceEntityId = "1",
            IdempotencyKey = "rep-tb-2",
            Narration = "Scan revenue",
            ActorUserId = Guid.NewGuid(),
            Lines =
            {
                new LedgerEventLine { AccountCode = "1100", DebitMinor  = 10_000_00, CurrencyCode = "GHS" },
                new LedgerEventLine { AccountCode = "4010", CreditMinor = 10_000_00, CurrencyCode = "GHS" }
            }
        });

        var reports = new FinancialReports(lg, coa);
        var tb = await reports.TrialBalanceAsync("GHS", new DateOnly(2026, 4, 30), Tenant);
        Assert.True(tb.IsBalanced);
        Assert.Equal(15_000_00, tb.TotalDebits.Minor);
        Assert.Equal(15_000_00, tb.TotalCredits.Minor);
        Assert.Equal("Rent — premises", tb.Rows.Single(r => r.AccountCode == "6100").AccountName);
    }

    [Fact]
    public async Task BalanceSheet_AssetsEqualLiabAndEquity()
    {
        await using var lg = _fx.NewLedger();
        await using var coa = _fx.NewCoa();
        const long Tenant = 8002;
        await new CoaService(coa).SeedGhanaStandardChartAsync(Tenant);
        var period = await new PeriodService(lg).CreateAsync(2026, 5, Tenant);
        var writer = new LedgerWriter(lg);

        await writer.PostAsync(new LedgerEvent
        {
            TenantId = Tenant,
            EffectiveDate = new DateOnly(2026, 5, 1),
            PeriodId = period.PeriodId,
            SourceModule = "test", SourceEntityType = "open", SourceEntityId = "x",
            IdempotencyKey = "rep-bs-open",
            Narration = "Initial capital",
            ActorUserId = Guid.NewGuid(),
            Lines =
            {
                new LedgerEventLine { AccountCode = "1030", DebitMinor  = 50_000_00, CurrencyCode = "GHS" },
                new LedgerEventLine { AccountCode = "3000", CreditMinor = 50_000_00, CurrencyCode = "GHS" }
            }
        });

        var reports = new FinancialReports(lg, coa);
        var bs = await reports.BalanceSheetAsync("GHS", new DateOnly(2026, 5, 31), Tenant);
        Assert.True(bs.IsBalanced, $"BS unbalanced: assets {bs.TotalAssets}, L+E {bs.TotalLiabilitiesAndEquity}");
        Assert.Equal(50_000_00, bs.TotalAssets.Minor);
    }

    [Fact]
    public async Task ProfitAndLoss_NetsIncomeMinusExpenses()
    {
        await using var lg = _fx.NewLedger();
        await using var coa = _fx.NewCoa();
        const long Tenant = 8003;
        await new CoaService(coa).SeedGhanaStandardChartAsync(Tenant);
        var period = await new PeriodService(lg).CreateAsync(2026, 6, Tenant);
        var writer = new LedgerWriter(lg);

        await writer.PostAsync(new LedgerEvent
        {
            TenantId = Tenant,
            EffectiveDate = new DateOnly(2026, 6, 4),
            PeriodId = period.PeriodId,
            SourceModule = "test", SourceEntityType = "rev", SourceEntityId = "1",
            IdempotencyKey = "rep-pl-rev",
            Narration = "Revenue",
            ActorUserId = Guid.NewGuid(),
            Lines =
            {
                new LedgerEventLine { AccountCode = "1100", DebitMinor  = 8_000_00, CurrencyCode = "GHS" },
                new LedgerEventLine { AccountCode = "4010", CreditMinor = 8_000_00, CurrencyCode = "GHS" }
            }
        });
        await writer.PostAsync(new LedgerEvent
        {
            TenantId = Tenant,
            EffectiveDate = new DateOnly(2026, 6, 9),
            PeriodId = period.PeriodId,
            SourceModule = "test", SourceEntityType = "exp", SourceEntityId = "1",
            IdempotencyKey = "rep-pl-exp",
            Narration = "Travel",
            ActorUserId = Guid.NewGuid(),
            Lines =
            {
                new LedgerEventLine { AccountCode = "6300", DebitMinor  = 2_000_00, CurrencyCode = "GHS" },
                new LedgerEventLine { AccountCode = "1010", CreditMinor = 2_000_00, CurrencyCode = "GHS" }
            }
        });

        var reports = new FinancialReports(lg, coa);
        var pl = await reports.ProfitAndLossAsync("GHS", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30), Tenant);
        Assert.Equal(8_000_00, pl.TotalIncome.Minor);
        Assert.Equal(2_000_00, pl.TotalExpenses.Minor);
        Assert.Equal(6_000_00, pl.NetResult.Minor);
    }

    [Fact]
    public async Task GlDetail_RunningBalanceMatchesEachRow()
    {
        await using var lg = _fx.NewLedger();
        await using var coa = _fx.NewCoa();
        const long Tenant = 8004;
        var period = await new PeriodService(lg).CreateAsync(2026, 7, Tenant);
        var writer = new LedgerWriter(lg);

        for (var i = 1; i <= 3; i++)
        {
            await writer.PostAsync(new LedgerEvent
            {
                TenantId = Tenant,
                EffectiveDate = new DateOnly(2026, 7, i),
                PeriodId = period.PeriodId,
                SourceModule = "test", SourceEntityType = "x", SourceEntityId = i.ToString(),
                IdempotencyKey = $"rep-gl-{i}",
                Narration = $"Day {i}",
                ActorUserId = Guid.NewGuid(),
                Lines =
                {
                    new LedgerEventLine { AccountCode = "1010", DebitMinor  = 100_00 * i, CurrencyCode = "GHS" },
                    new LedgerEventLine { AccountCode = "3000", CreditMinor = 100_00 * i, CurrencyCode = "GHS" }
                }
            });
        }

        var reports = new FinancialReports(lg, coa);
        var detail = await reports.GlDetailAsync("1010", "GHS", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31), Tenant);
        Assert.Equal(3, detail.Rows.Count);
        Assert.Equal(100_00 + 200_00 + 300_00, detail.ClosingBalance.Minor);
        Assert.Equal(100_00, detail.Rows[0].RunningBalance.Minor);
        Assert.Equal(300_00, detail.Rows[1].RunningBalance.Minor);
        Assert.Equal(600_00, detail.Rows[2].RunningBalance.Minor);
    }
}
