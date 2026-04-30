using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NickFinance.Banking;
using NickFinance.Ledger;
using Npgsql;
using Xunit;

namespace NickFinance.Ledger.Tests;

/// <summary>
/// DB-backed integration tests for <see cref="FxRevaluationService"/>. Lives
/// in NickFinance.Ledger.Tests because the contract under test is the kernel
/// <see cref="IFxRevaluationService"/>; the implementation, however, is in
/// NickFinance.Banking (where the rate persistence and revaluation log
/// already sit). Each test seeds its own period, balances, and rates against
/// a fresh <c>nickscan_fxreval_test</c> Postgres database so cases stay
/// isolated.
/// </summary>
public sealed class FxRevaluationFixture : IAsyncLifetime
{
    public string ConnectionString { get; }

    public FxRevaluationFixture()
    {
        var raw = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException(
                "NICKFINANCE_TEST_DB env var required for FxRevaluationServiceTests. Example: " +
                "Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=...");
        ConnectionString = Rewrite(raw, "nickscan_fxreval_test");
    }

    public LedgerDbContext NewLedger() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);

    public BankingDbContext NewBanking() =>
        new(new DbContextOptionsBuilder<BankingDbContext>().UseNpgsql(ConnectionString).Options);

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
            // Banking schema lives in the same DB as the ledger schema —
            // create just its tables on top of the already-created ledger
            // schema using the relational creator. Mirrors the pattern the
            // production banking-tests fixture uses.
            var creator = (IRelationalDatabaseCreator)bk.Database.GetService<IDatabaseCreator>();
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
        catch { /* swallow on teardown */ }
    }

    private static string Rewrite(string conn, string newName)
    {
        var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("Database=", StringComparison.OrdinalIgnoreCase))
                parts[i] = $"Database={newName}";
        }
        return string.Join(';', parts);
    }
}

[CollectionDefinition("FxRevaluation")]
public class FxRevaluationCollection : ICollectionFixture<FxRevaluationFixture> { }

[Collection("FxRevaluation")]
public class FxRevaluationServiceTests
{
    private readonly FxRevaluationFixture _fx;
    public FxRevaluationServiceTests(FxRevaluationFixture fx) => _fx = fx;

    // -----------------------------------------------------------------
    // Test cases
    // -----------------------------------------------------------------

    [Fact]
    public async Task FirstTimeRevaluation_PostsFullTranslation()
    {
        // 100,000 minor USD = $1,000.00 — at 16.20 USD→GHS the translation is
        // GHS 16,200.00 (1,620,000 minor). First time means no prior log row,
        // so the full balance × rate becomes the gain (DR 1040, CR 7100).
        await using var setup = await CleanWorldAsync();
        var period = await SeedOpenPeriodAsync(setup.Ledger, year: 2100, month: 1);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.20m, period.EndDate);
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "USD", debitMinor: 100_000);

        var svc = NewService(setup);
        var result = await svc.RevalueAsync(period.PeriodId, period.EndDate,
            new[] { "1040" }, Guid.NewGuid());

        Assert.NotEqual(Guid.Empty, result.LedgerEventId);
        Assert.False(result.WasIdempotentNoOp);
        Assert.Equal("GHS", result.FunctionalCurrency);
        Assert.Equal(2, result.LineCount);             // DR 1040 + CR 7100
        Assert.Equal(1_620_000, result.NetGainOrLossMinor);

        await using var verify = await ContextsAsync();
        var balance1040 = await new LedgerReader(verify.Ledger)
            .GetAccountBalanceAsync("1040", "GHS", period.EndDate);
        Assert.Equal(1_620_000, balance1040.Minor);

        var balance7100 = await new LedgerReader(verify.Ledger)
            .GetAccountBalanceAsync("7100", "GHS", period.EndDate);
        Assert.Equal(-1_620_000, balance7100.Minor);   // CR-natural; DR-CR is negative
    }

    [Fact]
    public async Task SubsequentRevaluation_Gain_PostsOnlyDelta()
    {
        // Period 1 sets the carry rate at 16.20; period 2 hits 16.50, so the
        // delta on a 100,000-minor USD balance is 0.30 × 100,000 = 30,000
        // minor GHS gain.
        await using var setup = await CleanWorldAsync();
        var period1 = await SeedOpenPeriodAsync(setup.Ledger, year: 2101, month: 1);
        var period2 = await SeedOpenPeriodAsync(setup.Ledger, year: 2101, month: 2);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.20m, period1.EndDate);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.50m, period2.EndDate);
        await SeedForeignBalanceAsync(setup.Ledger, period1.PeriodId, "1040", "USD", debitMinor: 100_000);

        var svc = NewService(setup);
        // First reval seeds the log (sanity-check, ignore the result here).
        await svc.RevalueAsync(period1.PeriodId, period1.EndDate, new[] { "1040" }, Guid.NewGuid());

        // Second reval — only the delta should land in the journal.
        var second = await svc.RevalueAsync(period2.PeriodId, period2.EndDate, new[] { "1040" }, Guid.NewGuid());

        Assert.False(second.WasIdempotentNoOp);
        Assert.Equal(30_000, second.NetGainOrLossMinor);
        Assert.Equal(2, second.LineCount);

        // Cumulative GHS balance on 1040 is now 16,200 + 300 = 16,500 GHS
        // (1,650,000 minor) — the original 1,620,000 from period 1 plus the
        // 30,000 delta from period 2.
        await using var verify = await ContextsAsync();
        var balance1040 = await new LedgerReader(verify.Ledger)
            .GetAccountBalanceAsync("1040", "GHS", period2.EndDate);
        Assert.Equal(1_650_000, balance1040.Minor);
    }

    [Fact]
    public async Task SubsequentRevaluation_Loss_PostsOnlyDelta()
    {
        // Period 1 anchors at 16.50; period 2 drops to 15.90 → delta -0.60 on
        // a 100,000 minor balance = -60,000 minor loss. Journal shape: DR 7110
        // 60,000 / CR 1040 60,000.
        await using var setup = await CleanWorldAsync();
        var period1 = await SeedOpenPeriodAsync(setup.Ledger, year: 2102, month: 1);
        var period2 = await SeedOpenPeriodAsync(setup.Ledger, year: 2102, month: 2);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.50m, period1.EndDate);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 15.90m, period2.EndDate);
        await SeedForeignBalanceAsync(setup.Ledger, period1.PeriodId, "1040", "USD", debitMinor: 100_000);

        var svc = NewService(setup);
        await svc.RevalueAsync(period1.PeriodId, period1.EndDate, new[] { "1040" }, Guid.NewGuid());
        var second = await svc.RevalueAsync(period2.PeriodId, period2.EndDate, new[] { "1040" }, Guid.NewGuid());

        Assert.Equal(-60_000, second.NetGainOrLossMinor);

        await using var verify = await ContextsAsync();
        var balance7110 = await new LedgerReader(verify.Ledger)
            .GetAccountBalanceAsync("7110", "GHS", period2.EndDate);
        Assert.Equal(60_000, balance7110.Minor);     // DR-natural — debit = positive
    }

    [Fact]
    public async Task IdempotentReRun_ReturnsExistingEventWithoutDoublePosting()
    {
        await using var setup = await CleanWorldAsync();
        var period = await SeedOpenPeriodAsync(setup.Ledger, year: 2103, month: 1);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.20m, period.EndDate);
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "USD", debitMinor: 100_000);

        var svc = NewService(setup);
        var first = await svc.RevalueAsync(period.PeriodId, period.EndDate, new[] { "1040" }, Guid.NewGuid());
        var second = await svc.RevalueAsync(period.PeriodId, period.EndDate, new[] { "1040" }, Guid.NewGuid());

        Assert.False(first.WasIdempotentNoOp);
        Assert.True(second.WasIdempotentNoOp);
        Assert.Equal(first.LedgerEventId, second.LedgerEventId);

        // Only one event should exist in the ledger for the idempotency key.
        await using var verify = await ContextsAsync();
        var idempotencyKey = $"fxreval:{period.PeriodId:N}:{period.EndDate:yyyy-MM-dd}";
        var count = await verify.Ledger.Events.CountAsync(e => e.IdempotencyKey == idempotencyKey);
        Assert.Equal(1, count);

        // And only one revaluation-log row per (account, currency, period).
        var logCount = await verify.Banking.FxRevaluationLogs
            .CountAsync(l => l.PeriodId == period.PeriodId && l.GlAccount == "1040" && l.CurrencyCode == "USD");
        Assert.Equal(1, logCount);
    }

    [Fact]
    public async Task MissingFxRate_Throws()
    {
        await using var setup = await CleanWorldAsync();
        var period = await SeedOpenPeriodAsync(setup.Ledger, year: 2104, month: 1);
        // No EUR rate seeded — the converter has no fallback.
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "EUR", debitMinor: 50_000);

        var svc = NewService(setup);
        await Assert.ThrowsAsync<MissingFxRateException>(() =>
            svc.RevalueAsync(period.PeriodId, period.EndDate, new[] { "1040" }, Guid.NewGuid()));
    }

    [Fact]
    public async Task MultipleCurrenciesOnOneAccount_ProducesOneEventWithLinesPerCurrency()
    {
        // 1040 holds both USD and EUR balances. One event, four lines (DR/CR
        // pair for USD, DR/CR pair for EUR), single combined idempotency key.
        await using var setup = await CleanWorldAsync();
        var period = await SeedOpenPeriodAsync(setup.Ledger, year: 2105, month: 1);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.20m, period.EndDate);
        await SeedFxRateAsync(setup.Banking, "EUR", "GHS", 17.40m, period.EndDate);
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "USD", debitMinor: 100_000);
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "EUR", debitMinor: 50_000);

        var svc = NewService(setup);
        var result = await svc.RevalueAsync(period.PeriodId, period.EndDate, new[] { "1040" }, Guid.NewGuid());

        Assert.False(result.WasIdempotentNoOp);
        Assert.Equal(4, result.LineCount);
        // 100,000 * 16.20 + 50,000 * 17.40 = 1,620,000 + 870,000 = 2,490,000
        Assert.Equal(2_490_000, result.NetGainOrLossMinor);

        // Two log rows — one per (account, currency) pair.
        await using var verify = await ContextsAsync();
        var logs = await verify.Banking.FxRevaluationLogs
            .Where(l => l.PeriodId == period.PeriodId && l.GlAccount == "1040")
            .OrderBy(l => l.CurrencyCode)
            .ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.Equal("EUR", logs[0].CurrencyCode);
        Assert.Equal("USD", logs[1].CurrencyCode);
    }

    [Fact]
    public async Task ZeroBalance_IsSkipped()
    {
        // An account with offsetting DR/CR in the same currency nets to zero
        // — no revaluation line, no log row, zero net.
        await using var setup = await CleanWorldAsync();
        var period = await SeedOpenPeriodAsync(setup.Ledger, year: 2106, month: 1);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.20m, period.EndDate);
        // Post both legs in USD so the sum nets to zero.
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "USD",
            debitMinor: 50_000);
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "USD",
            debitMinor: 0, creditMinor: 50_000);

        var svc = NewService(setup);
        var result = await svc.RevalueAsync(period.PeriodId, period.EndDate, new[] { "1040" }, Guid.NewGuid());

        // Empty journal — Guid.Empty event id, zero lines, zero net.
        Assert.Equal(Guid.Empty, result.LedgerEventId);
        Assert.Equal(0, result.LineCount);
        Assert.Equal(0, result.NetGainOrLossMinor);
        Assert.False(result.WasIdempotentNoOp);

        await using var verify = await ContextsAsync();
        Assert.Equal(0, await verify.Banking.FxRevaluationLogs
            .CountAsync(l => l.PeriodId == period.PeriodId));
    }

    [Fact]
    public async Task FunctionalCurrency_IsSkipped()
    {
        // 1040 has GHS activity (the functional currency). No revaluation
        // should be produced — translating GHS into GHS is a no-op.
        await using var setup = await CleanWorldAsync();
        var period = await SeedOpenPeriodAsync(setup.Ledger, year: 2107, month: 1);
        await SeedFxRateAsync(setup.Banking, "USD", "GHS", 16.20m, period.EndDate);
        await SeedForeignBalanceAsync(setup.Ledger, period.PeriodId, "1040", "GHS", debitMinor: 75_000);

        var svc = NewService(setup);
        var result = await svc.RevalueAsync(period.PeriodId, period.EndDate, new[] { "1040" }, Guid.NewGuid());

        Assert.Equal(0, result.LineCount);
        Assert.Equal(0, result.NetGainOrLossMinor);
        Assert.Equal(Guid.Empty, result.LedgerEventId);
    }

    // -----------------------------------------------------------------
    // Setup helpers
    // -----------------------------------------------------------------

    private sealed class WorldContexts(LedgerDbContext ledger, BankingDbContext banking) : IAsyncDisposable
    {
        public LedgerDbContext Ledger { get; } = ledger;
        public BankingDbContext Banking { get; } = banking;
        public async ValueTask DisposeAsync()
        {
            await Ledger.DisposeAsync();
            await Banking.DisposeAsync();
        }
    }

    private async Task<WorldContexts> ContextsAsync()
    {
        await Task.CompletedTask;
        return new WorldContexts(_fx.NewLedger(), _fx.NewBanking());
    }

    /// <summary>
    /// Wipes every test-mutable table — periods, events, lines, fx rates,
    /// reval log — so each [Fact] starts from a clean slate. The fixture
    /// itself only resets between test runs (it spins up a fresh DB once),
    /// so per-test cleanup is needed to keep cases independent.
    /// </summary>
    private async Task<WorldContexts> CleanWorldAsync()
    {
        var ctx = await ContextsAsync();

        ctx.Banking.FxRevaluationLogs.RemoveRange(await ctx.Banking.FxRevaluationLogs.ToListAsync());
        ctx.Banking.FxRates.RemoveRange(await ctx.Banking.FxRates.ToListAsync());
        await ctx.Banking.SaveChangesAsync();

        // ledger_events / ledger_event_lines have an append-only trigger plus
        // a deferred balance constraint. TRUNCATE … RESTART IDENTITY CASCADE
        // bypasses the per-row trigger and runs in its own statement so the
        // deferred balance check is satisfied before we move on.
        await ctx.Ledger.Database.ExecuteSqlRawAsync(
            "TRUNCATE finance.ledger_event_lines, finance.ledger_events, finance.accounting_periods RESTART IDENTITY CASCADE;");
        return ctx;
    }

    private static FxRevaluationService NewService(WorldContexts ctx)
    {
        var reader = new LedgerReader(ctx.Ledger);
        var writer = new LedgerWriter(ctx.Ledger);
        var converter = new FxRateService(ctx.Banking);
        return new FxRevaluationService(reader, writer, converter, ctx.Ledger, ctx.Banking);
    }

    private static async Task<AccountingPeriod> SeedOpenPeriodAsync(LedgerDbContext db, int year, byte month)
    {
        var svc = new PeriodService(db);
        return await svc.CreateAsync(year, month);
    }

    private static async Task SeedFxRateAsync(BankingDbContext db, string from, string to, decimal rate, DateOnly asOf, long tenantId = 1)
    {
        db.FxRates.Add(new FxRate
        {
            FromCurrency = from,
            ToCurrency = to,
            Rate = rate,
            AsOfDate = asOf,
            Source = "test",
            RecordedAt = DateTimeOffset.UtcNow,
            RecordedByUserId = Guid.Empty,
            TenantId = tenantId
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Posts a balanced two-line journal that puts the requested
    /// foreign-currency balance on <paramref name="accountCode"/>. The
    /// counter-leg uses a generic suspense-style account in the same currency
    /// so the kernel writer's same-currency rule is respected.
    /// </summary>
    private static async Task SeedForeignBalanceAsync(
        LedgerDbContext db,
        Guid periodId,
        string accountCode,
        string currency,
        long debitMinor,
        long creditMinor = 0,
        long tenantId = 1)
    {
        var writer = new LedgerWriter(db);
        // If the test wants a debit on the target account, the counter-leg
        // is a credit on a placeholder account, both in the same currency.
        var counter = "9999-TEST-COUNTER";
        var lines = new List<LedgerEventLine>();
        if (debitMinor > 0)
        {
            lines.Add(new LedgerEventLine { AccountCode = accountCode, DebitMinor = debitMinor, CreditMinor = 0, CurrencyCode = currency });
            lines.Add(new LedgerEventLine { AccountCode = counter, DebitMinor = 0, CreditMinor = debitMinor, CurrencyCode = currency });
        }
        if (creditMinor > 0)
        {
            lines.Add(new LedgerEventLine { AccountCode = counter, DebitMinor = creditMinor, CreditMinor = 0, CurrencyCode = currency });
            lines.Add(new LedgerEventLine { AccountCode = accountCode, DebitMinor = 0, CreditMinor = creditMinor, CurrencyCode = currency });
        }
        if (lines.Count == 0) return;

        var period = await db.Periods.AsNoTracking().FirstAsync(p => p.PeriodId == periodId);
        await writer.PostAsync(new LedgerEvent
        {
            TenantId = tenantId,
            EffectiveDate = period.StartDate,
            PeriodId = periodId,
            SourceModule = "test",
            SourceEntityType = "TestSeed",
            SourceEntityId = Guid.NewGuid().ToString(),
            IdempotencyKey = "seed-" + Guid.NewGuid(),
            Narration = "test seed",
            ActorUserId = Guid.NewGuid(),
            Lines = lines
        });
    }
}
