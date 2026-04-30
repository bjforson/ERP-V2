using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using Npgsql;
using Xunit;

namespace NickFinance.Identity.Tests;

/// <summary>
/// Postgres-backed fixture for the SodService tests. Mirrors the
/// pattern in <c>NickFinance.AR.Tests.ArFixture</c> — rewrites the
/// database name in the connection string so the tests own a private
/// schema, applies the EF migrations once, and tears the DB down on
/// dispose.
/// </summary>
public sealed class SodFixture : IAsyncLifetime
{
    public string ConnectionString { get; }

    public SodFixture()
    {
        var raw = Environment.GetEnvironmentVariable("NICKFINANCE_TEST_DB")
            ?? throw new InvalidOperationException("NICKFINANCE_TEST_DB env var required.");
        ConnectionString = RewriteDb(raw, "nickscan_sod_test");
    }

    public IdentityDbContext NewIdentity() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = NewIdentity();
        await db.Database.EnsureDeletedAsync();
        // Use MigrateAsync to get the seeded roles + the audit_firm column.
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var db = NewIdentity();
            await db.Database.EnsureDeletedAsync();
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

[CollectionDefinition("Sod")]
public class SodCollection : ICollectionFixture<SodFixture> { }
