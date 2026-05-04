using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Tenancy.Database.Services;
using NickERP.Platform.Tenancy.Database.Workers;

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
            {
                npgsql.MigrationsAssembly(typeof(TenancyDbContext).Assembly.GetName().Name);
                // H3 — keep EF Core's history table inside the tenancy
                // schema so nscim_app never needs CREATE on `public`.
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "tenancy");
            });
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

    /// <summary>
    /// Sprint 18 — register <see cref="ITenantLifecycleService"/> +
    /// <see cref="ITenantPurgeOrchestrator"/> for hosts that surface the
    /// admin lifecycle UI (the portal). Reads downstream-DB connection
    /// strings from env vars: <c>NICKERP_PLATFORM_DB_CONNECTION</c>,
    /// <c>NICKERP_INSPECTION_DB_CONNECTION</c>,
    /// <c>NICKERP_NICKFINANCE_DB_CONNECTION</c>. Connection strings can
    /// also be supplied via the optional configure delegate for tests.
    /// </summary>
    public static IServiceCollection AddNickErpTenantLifecycle(
        this IServiceCollection services,
        Action<TenantPurgeOrchestratorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp =>
        {
            var opts = new TenantPurgeOrchestratorOptions
            {
                PlatformConnectionString =
                    Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION"),
                InspectionConnectionString =
                    Environment.GetEnvironmentVariable("NICKERP_INSPECTION_DB_CONNECTION"),
                NickFinanceConnectionString =
                    Environment.GetEnvironmentVariable("NICKERP_NICKFINANCE_DB_CONNECTION"),
            };
            configure?.Invoke(opts);
            return opts;
        });
        services.AddScoped<ITenantPurgeOrchestrator, TenantPurgeOrchestrator>();
        services.AddScoped<ITenantLifecycleService, TenantLifecycleService>();
        return services;
    }

    /// <summary>
    /// Sprint 25 — register <see cref="ITenantExportService"/> +
    /// <see cref="TenantExportRunner"/> for hosts that surface the
    /// admin Exports card (the portal). Reads connection strings from
    /// the same env vars as the lifecycle wireup. Pass a configure
    /// delegate to override storage path / retention / concurrency for
    /// tests.
    /// </summary>
    public static IServiceCollection AddNickErpTenantExport(
        this IServiceCollection services,
        Action<TenantExportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(sp =>
        {
            var opts = new TenantExportOptions
            {
                PlatformConnectionString =
                    Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION"),
                InspectionConnectionString =
                    Environment.GetEnvironmentVariable("NICKERP_INSPECTION_DB_CONNECTION"),
                NickFinanceConnectionString =
                    Environment.GetEnvironmentVariable("NICKERP_NICKFINANCE_DB_CONNECTION"),
            };
            configure?.Invoke(opts);
            return opts;
        });
        services.AddScoped<ITenantExportService, TenantExportService>();
        // Runner runs in the host as a hosted service. Single instance
        // per host; concurrency capped by MaxConcurrentExports (default 2).
        services.AddHostedService<TenantExportRunner>();
        return services;
    }
}
