using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Platform.Audit.Database;

/// <summary>
/// DI registration for the database-backed audit + event-bus layer.
/// </summary>
public static class AuditDatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Wire <see cref="AuditDbContext"/>, the in-process <see cref="IEventBus"/>,
    /// and the DB-backed <see cref="IEventPublisher"/>. Reads connection string
    /// from <paramref name="connectionString"/>, falling back to env var
    /// <c>NICKERP_PLATFORM_DB_CONNECTION</c>.
    /// </summary>
    public static IServiceCollection AddNickErpAuditCore(
        this IServiceCollection services,
        string? connectionString = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var resolved = connectionString
            ?? Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Postgres connection string for nickerp_platform was not provided and "
                + "NICKERP_PLATFORM_DB_CONNECTION env var is not set.");

        services.AddDbContext<AuditDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(resolved, npgsql =>
                npgsql.MigrationsAssembly(typeof(AuditDbContext).Assembly.GetName().Name));
            // Phase F1 — push app.tenant_id to Postgres on connection open
            // (RLS) and stamp TenantId on inserts (defense-in-depth).
            opts.AddInterceptors(
                sp.GetRequiredService<TenantConnectionInterceptor>(),
                sp.GetRequiredService<TenantOwnedEntityInterceptor>());
        });

        services.AddSingleton<IEventBus, InProcessEventBus>();
        services.AddScoped<IEventPublisher, DbEventPublisher>();
        return services;
    }
}
