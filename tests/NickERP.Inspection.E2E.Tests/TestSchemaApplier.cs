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
        await ApplyAsync<IdentityDbContext>(
            platformConnectionString,
            opts => new IdentityDbContext(opts),
            ct);

        await ApplyAsync<AuditDbContext>(
            platformConnectionString,
            opts => new AuditDbContext(opts),
            ct);

        await ApplyAsync<TenancyDbContext>(
            platformConnectionString,
            opts => new TenancyDbContext(opts),
            ct);

        await ApplyAsync<InspectionDbContext>(
            inspectionConnectionString,
            opts => new InspectionDbContext(opts),
            ct);
    }

    private static async Task ApplyAsync<TContext>(
        string connectionString,
        Func<DbContextOptions<TContext>, TContext> factory,
        CancellationToken ct)
        where TContext : DbContext
    {
        var optsBuilder = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(TContext).Assembly.GetName().Name));
        await using var db = factory(optsBuilder.Options);
        await db.Database.MigrateAsync(ct);
    }
}
