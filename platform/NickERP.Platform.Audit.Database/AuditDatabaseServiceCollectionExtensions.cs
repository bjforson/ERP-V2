using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Audit.Database.Services;
using NickERP.Platform.Audit.Database.Services.NotificationRules;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Telemetry;
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
            {
                npgsql.MigrationsAssembly(typeof(AuditDbContext).Assembly.GetName().Name);
                // H3 — keep EF Core's history table inside the audit
                // schema so nscim_app never needs CREATE on `public`.
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "audit");
            });
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

    /// <summary>
    /// Sprint 8 P3 — register the <see cref="AuditNotificationProjector"/>
    /// background service plus the three hardcoded notification rules.
    ///
    /// <para>
    /// Rules are registered as <c>Scoped</c> rather than <c>Singleton</c>
    /// because <see cref="CaseVerdictRenderedRule"/> takes a scoped
    /// <see cref="AuditDbContext"/> for its case-opener lookup. The
    /// projector creates a per-tick scope and resolves
    /// <c>IEnumerable&lt;INotificationRule&gt;</c> from it; rules that
    /// don't take a scoped dep (CaseOpenedRule, CaseAssignedRule) are
    /// fine being constructed per scope.
    /// </para>
    ///
    /// <para>
    /// Call from any host that wants to project audit events into
    /// notifications — typically the inspection / portal app. The
    /// underlying <see cref="AuditDbContext"/> registration is from
    /// <see cref="AddNickErpAuditCore"/>; the projector reuses it.
    /// </para>
    /// </summary>
    public static IServiceCollection AddNickErpAuditNotifications(
        this IServiceCollection services,
        Action<AuditNotificationProjectorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AuditNotificationProjectorOptions>();
        if (configure is not null) services.Configure(configure);

        services.AddScoped<INotificationRule, CaseOpenedRule>();
        services.AddScoped<INotificationRule, CaseAssignedRule>();
        services.AddScoped<INotificationRule, CaseVerdictRenderedRule>();

        // Sprint 9 / FU-host-status — register the projector as a
        // singleton, then resolve it for both the hosted-service slot
        // AND the IBackgroundServiceProbe slot. Critical invariant: ONE
        // projector instance — if AddHostedService<T>() were used alone
        // the host would create a separate instance and the probe
        // registration would resolve a different one (always reporting
        // "never ticked").
        services.AddSingleton<AuditNotificationProjector>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditNotificationProjector>());
        services.AddSingleton<IBackgroundServiceProbe>(sp => sp.GetRequiredService<AuditNotificationProjector>());
        return services;
    }
}
