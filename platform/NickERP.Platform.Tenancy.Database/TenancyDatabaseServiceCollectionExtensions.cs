using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddDbContext<TenancyDbContext>(opts => opts.UseNpgsql(resolved, npgsql =>
            npgsql.MigrationsAssembly(typeof(TenancyDbContext).Assembly.GetName().Name)));

        return services;
    }
}
