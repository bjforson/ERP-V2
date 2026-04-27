using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

// NickERP.Platform.Tenancy is referenced via ProjectReference; the
// interceptor types live in the parent namespace.
namespace NickERP.Platform.Tenancy.Database;

/// <summary>
/// DI registration for the database-backed tenants table.
/// Hosts that need to read/write tenants (the future tenancy admin REST API,
/// integration tests, ops scripts) call <see cref="AddNickErpTenancyCore"/>;
/// regular module hosts only need <c>AddNickErpTenancy()</c> (the in-process
/// abstractions) since they read tenant data implicitly via
/// <see cref="Entities.ITenantOwned"/>.
/// </summary>
public static class TenancyDatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Wire <see cref="TenancyDbContext"/> against the configured Postgres
    /// connection. Falls back to env var <c>NICKERP_PLATFORM_DB_CONNECTION</c>.
    /// </summary>
    public static IServiceCollection AddNickErpTenancyCore(
        this IServiceCollection services,
        string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var resolved = connectionString
            ?? Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Postgres connection string for nickerp_platform was not provided and "
                + "NICKERP_PLATFORM_DB_CONNECTION env var is not set.");

        services.AddDbContext<TenancyDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(resolved, npgsql =>
                npgsql.MigrationsAssembly(typeof(TenancyDbContext).Assembly.GetName().Name));
            // Phase F1 — push app.tenant_id to Postgres on connection open
            // (RLS) and stamp TenantId on inserts. Note: tenancy.tenants is
            // intentionally NOT under RLS (root of the tenant graph), but
            // future tenant-owned tables in this schema will be — so the
            // interceptors stay attached.
            opts.AddInterceptors(
                sp.GetRequiredService<TenantConnectionInterceptor>(),
                sp.GetRequiredService<TenantOwnedEntityInterceptor>());
        });

        return services;
    }
}
