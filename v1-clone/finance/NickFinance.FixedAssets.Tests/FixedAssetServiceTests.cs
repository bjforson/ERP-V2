using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NickFinance.FixedAssets;
using NickFinance.Ledger;
using Npgsql;
using Xunit;

namespace NickFinance.FixedAssets.Tests;

public sealed class FaFixture : IAsyncLifetime
{
    public string ConnectionString { get; }
    public FaFixture()
    {
        var raw = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException("NICKFINANCE_TEST_DB env var required.");
        ConnectionString = Rewrite(raw, "nickscan_fa_test");
    }
    public LedgerDbContext NewLedger() => new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);
    public FixedAssetsDbContext NewFa() => new(new DbContextOptionsBuilder<FixedAssetsDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using (var lg = NewLedger())
        {
            await lg.Database.EnsureDeletedAsync();
            await lg.Database.EnsureCreatedAsync();
            await SchemaBootstrap.ApplyConstraintsAsync(lg);
        }
        await using (var fa = NewFa())
        {
            var creator = (IRelationalDatabaseCreator)fa.Database.GetService<IDatabaseCreator>();
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

[CollectionDefinition("FA")] public class FaCollection : ICollectionFixture<FaFixture> { }

[Collection("FA")]
public class FixedAssetServiceTests
{
    private readonly FaFixture _fx;
    public FixedAssetServiceTests(FaFixture fx) => _fx = fx;

    [Fact]
    public async Task StraightLine_PostsMonthlyDepreciationJournal()
    {
        await using var lg = _fx.NewLedger();
        await using var fa = _fx.NewFa();
        var period = await new PeriodService(lg).CreateAsync(2026, 4);
        var svc = new FixedAssetService(fa, new LedgerWriter(lg));

        // Acquisition: GHS 36,000, 36-month life, no salvage → 1,000/month.
        var asset = await svc.RegisterAsync(new RegisterAssetRequest(
            "SCAN-TEMA-001", "Tema scanner", AssetCategory.Scanner,
            new DateOnly(2026, 4, 1), 36_000_00, 36));
        Assert.Equal(0, asset.AccumulatedDepreciationMinor);

        var posted = await svc.PostMonthlyDepreciationAsync(2026, 4, Guid.NewGuid(), period.PeriodId);
        Assert.Equal(1, posted);

        await using var fa2 = _fx.NewFa();
        var after = await fa2.Assets.FirstAsync(a => a.FixedAssetId == asset.FixedAssetId);
        Assert.Equal(1_000_00, after.AccumulatedDepreciationMinor);
        Assert.Equal(35_000_00, after.NetBookValueMinor);

        // Re-running for the same period is idempotent.
        var second = await svc.PostMonthlyDepreciationAsync(2026, 4, Guid.NewGuid(), period.PeriodId);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task DisposalAtNbv_PostsZeroGainLoss()
    {
        await using var lg = _fx.NewLedger();
        await using var fa = _fx.NewFa();
        var period = await new PeriodService(lg).CreateAsync(2026, 5);
        var svc = new FixedAssetService(fa, new LedgerWriter(lg));

        var asset = await svc.RegisterAsync(new RegisterAssetRequest(
            "VEH-001", "Truck", AssetCategory.Vehicle,
            new DateOnly(2026, 5, 1), 10_000_00, 10));
        // Run depreciation once.
        await svc.PostMonthlyDepreciationAsync(2026, 5, Guid.NewGuid(), period.PeriodId);

        // Sell at NBV (= 9,000) → no gain/loss leg.
        var disposed = await svc.DisposeAsync(asset.FixedAssetId, new DateOnly(2026, 5, 31), 9_000_00, Guid.NewGuid(), period.PeriodId);
        Assert.Equal(AssetStatus.Disposed, disposed.Status);
        Assert.Equal(9_000_00, disposed.DisposalProceedsMinor);

        // Inspect the journal — should balance with no 4900/6900 line.
        await using var lg2 = _fx.NewLedger();
        var ev = await lg2.Events.Include(e => e.Lines).FirstAsync(e => e.IdempotencyKey == $"fa:{asset.FixedAssetId:N}:dispose");
        Assert.Equal(ev.Lines.Sum(l => l.DebitMinor), ev.Lines.Sum(l => l.CreditMinor));
        Assert.DoesNotContain(ev.Lines, l => l.AccountCode == "4900" || l.AccountCode == "6900");
    }
}
