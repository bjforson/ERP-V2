using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NickFinance.Ledger;
using NickFinance.PettyCash;
using Xunit;

namespace NickFinance.PettyCash.Tests;

/// <summary>
/// Shared fixture. Spins up <c>nickscan_pettycash_test</c> on the local
/// Postgres so both the Ledger schema (<c>finance.*</c>) and the Petty Cash
/// schema (<c>petty_cash.*</c>) coexist in one DB — exactly how they will
/// in production once wired into <c>nickhr</c>.
/// </summary>
public sealed class PettyCashFixture : IAsyncLifetime
{
    public const string EnvVar = "NICKFINANCE_TEST_DB";
    public string ConnectionString { get; }

    public PettyCashFixture()
    {
        // Reuse the same env var the Ledger suite uses, but rewrite the
        // database name so the two suites don't trample each other.
        var raw = Environment.GetEnvironmentVariable(EnvVar)
            ?? throw new InvalidOperationException(
                $"{EnvVar} env var is required for Petty Cash tests. Example: "
                + "Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=...");
        ConnectionString = RewriteDatabase(raw, "nickscan_pettycash_test");
    }

    public LedgerDbContext CreateLedger() =>
        new(new DbContextOptionsBuilder<LedgerDbContext>().UseNpgsql(ConnectionString).Options);

    public PettyCashDbContext CreatePettyCash() =>
        new(new DbContextOptionsBuilder<PettyCashDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        // Provision both schemas in one fresh DB. Ledger first (so its
        // triggers exist), then Petty Cash (its tables are independent).
        await using (var ledger = CreateLedger())
        {
            await ledger.Database.EnsureDeletedAsync();
            await ledger.Database.EnsureCreatedAsync();
            await SchemaBootstrap.ApplyConstraintsAsync(ledger);
        }
        await using (var pc = CreatePettyCash())
        {
            // EnsureCreatedAsync skips when the DB already exists (the
            // Ledger context just created it), so call the underlying
            // table creator directly — it only creates tables this
            // context knows about (everything in the petty_cash schema).
            var creator = (IRelationalDatabaseCreator)pc.Database.GetService<IDatabaseCreator>();
            await creator.CreateTablesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var pc = CreatePettyCash();
            await pc.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Best-effort tear-down. The DB is throwaway; the next run resets it.
        }
    }

    private static string RewriteDatabase(string conn, string newDbName)
    {
        var parts = conn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rebuilt = new List<string>();
        var saw = false;
        foreach (var p in parts)
        {
            var ix = p.IndexOf('=', StringComparison.Ordinal);
            if (ix < 0) { rebuilt.Add(p); continue; }
            var key = p[..ix].Trim();
            if (string.Equals(key, "Database", StringComparison.OrdinalIgnoreCase))
            {
                rebuilt.Add($"Database={newDbName}");
                saw = true;
            }
            else
            {
                rebuilt.Add(p);
            }
        }
        if (!saw) rebuilt.Add($"Database={newDbName}");
        return string.Join(';', rebuilt);
    }
}

[CollectionDefinition("PettyCash")]
public class PettyCashCollection : ICollectionFixture<PettyCashFixture> { }
