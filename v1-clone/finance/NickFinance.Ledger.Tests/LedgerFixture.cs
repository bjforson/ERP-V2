using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using Xunit;

namespace NickFinance.Ledger.Tests;

/// <summary>
/// Shared fixture: spins up a fresh `nickscan_finance_test` schema on the
/// local Postgres for the test run. EnsureCreatedAsync runs once; each
/// test opens its own DbContext.
///
/// Connection string comes from env var NICKFINANCE_TEST_DB. If unset,
/// fixture throws — Ledger tests are DB-backed by design. Set it in
/// your shell / CI before running:
///     NICKFINANCE_TEST_DB="Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=..."
/// </summary>
public sealed class LedgerFixture : IAsyncLifetime
{
    public string ConnectionString { get; }

    public LedgerFixture()
    {
        ConnectionString = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException(
                "NICKFINANCE_TEST_DB env var is required for Ledger tests. Example: " +
                "Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=...");
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        await SchemaBootstrap.ApplyConstraintsAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }

    public LedgerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new LedgerDbContext(options);
    }
}

[CollectionDefinition("Ledger")]
public class LedgerCollection : ICollectionFixture<LedgerFixture> { }
