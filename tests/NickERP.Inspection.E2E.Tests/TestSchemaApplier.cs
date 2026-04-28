using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Applies the four DbContext migration sets the host runs at startup —
/// Identity, Audit, Tenancy, Inspection — but standalone, so the e2e
/// test can seed configuration rows BEFORE the host starts. That matters
/// because <see cref="Web.Services.ScannerIngestionWorker"/> runs its
/// first discovery cycle immediately on startup; if the seed lands
/// AFTER, the test waits a full <c>DiscoveryInterval</c> (60s) for the
/// next cycle and overshoots the 2-minute D4 budget.
///
/// Each context is built with a no-op interceptor list — the production
/// host wires <c>TenantConnectionInterceptor</c> + <c>TenantOwnedEntityInterceptor</c>,
/// but those are connection-time concerns, not migration concerns. The
/// migrations themselves don't need a tenant context (DDL doesn't go
/// through RLS, and the seed inserts in the platform DB are exempt:
/// tenancy.tenants is intentionally not under RLS, and identity seeds
/// happen with a wide-open postgres connection that has BYPASSRLS).
/// </summary>
internal static class TestSchemaApplier
{
    public static async Task ApplyAllAsync(
        string platformConnectionString,
        string inspectionConnectionString,
        CancellationToken ct = default)
    {
        // Identity, Audit, Tenancy share the platform DB.
        // H3 — each context's __EFMigrationsHistory lives in its own
        // schema (see DbContext config); pass the same schema here so the
        // standalone migrate sees the relocated history tables.
        await ApplyAsync<IdentityDbContext>(
            platformConnectionString,
            opts => new IdentityDbContext(opts),
            historySchema: "identity",
            ct);

        await ApplyAsync<AuditDbContext>(
            platformConnectionString,
            opts => new AuditDbContext(opts),
            historySchema: "audit",
            ct);

        await ApplyAsync<TenancyDbContext>(
            platformConnectionString,
            opts => new TenancyDbContext(opts),
            historySchema: "tenancy",
            ct);

        await ApplyAsync<InspectionDbContext>(
            inspectionConnectionString,
            opts => new InspectionDbContext(opts),
            historySchema: "inspection",
            ct);
    }

    private static async Task ApplyAsync<TContext>(
        string connectionString,
        Func<DbContextOptions<TContext>, TContext> factory,
        string historySchema,
        CancellationToken ct)
        where TContext : DbContext
    {
        var optsBuilder = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(TContext).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", historySchema);
            });
        await using var db = factory(optsBuilder.Options);
        await db.Database.MigrateAsync(ct);
    }
}
