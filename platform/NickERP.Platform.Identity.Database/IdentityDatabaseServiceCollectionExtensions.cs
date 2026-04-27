using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Identity.Database.Services;
using NickERP.Platform.Identity.Services;
using NickERP.Platform.Tenancy;

namespace NickERP.Platform.Identity.Database;

/// <summary>
/// DI registration for the database-backed <see cref="IIdentityResolver"/>.
/// Register this AFTER <c>services.AddNickErpIdentity(...)</c> from the
/// abstractions package — the resolver depends on <see cref="Auth.ICfJwtValidator"/>
/// and <see cref="Auth.CfAccessAuthenticationOptions"/> registered there.
/// </summary>
public static class IdentityDatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Wire <see cref="IdentityDbContext"/> + the DB-backed
    /// <see cref="IIdentityResolver"/>. Reads connection string from
    /// <paramref name="connectionString"/>; falls back to env var
    /// <c>NICKERP_PLATFORM_DB_CONNECTION</c> if null.
    /// </summary>
    public static IServiceCollection AddNickErpIdentityCore(
        this IServiceCollection services,
        string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var resolved = connectionString
            ?? Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Postgres connection string for nickerp_platform was not provided and "
                + "NICKERP_PLATFORM_DB_CONNECTION env var is not set.");

        services.AddDbContext<IdentityDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(resolved, npgsql =>
                npgsql.MigrationsAssembly(typeof(IdentityDbContext).Assembly.GetName().Name));
            // Phase F1 — push app.tenant_id to Postgres on connection open
            // (RLS) and stamp TenantId on inserts (defense-in-depth).
            opts.AddInterceptors(
                sp.GetRequiredService<TenantConnectionInterceptor>(),
                sp.GetRequiredService<TenantOwnedEntityInterceptor>());
        });

        services.AddScoped<IIdentityResolver, DbIdentityResolver>();
        return services;
    }
}
