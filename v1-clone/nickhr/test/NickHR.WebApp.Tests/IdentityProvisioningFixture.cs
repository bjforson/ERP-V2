using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using Xunit;

namespace NickHR.WebApp.Tests;

/// <summary>
/// Throwaway Postgres DB used by <see cref="IdentityProvisioningServiceTests"/>.
/// Mirrors the Identity-suite fixture in
/// <c>platform/NickERP.Platform.Identity.Tests</c> — same NICKFINANCE_TEST_DB
/// env-var convention, just rewritten to its own DB
/// (<c>nickhr_provisioning_test</c>) so two suites running in parallel don't
/// trample each other.
/// </summary>
public sealed class IdentityProvisioningFixture : IAsyncLifetime
{
    public const string EnvVar = "NICKFINANCE_TEST_DB";
    public string ConnectionString { get; }

    public IdentityProvisioningFixture()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar)
            ?? throw new InvalidOperationException(
                $"{EnvVar} env var is required for NickHR identity-provisioning tests. Example: "
                + "Host=localhost;Port=5432;Database=nickscan_finance_test;Username=postgres;Password=...");
        ConnectionString = RewriteDatabase(raw, "nickhr_provisioning_test");
    }

    public IdentityDbContext CreateIdentity(ITenantAccessor? tenant = null) =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(ConnectionString).Options, tenant);

    public async Task InitializeAsync()
    {
        await using var id = CreateIdentity();
        await id.Database.EnsureDeletedAsync();
        await id.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var id = CreateIdentity();
            await id.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Best-effort tear-down. Next run resets the DB anyway.
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

[CollectionDefinition("IdentityProvisioning")]
public class IdentityProvisioningCollection : ICollectionFixture<IdentityProvisioningFixture> { }
