using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Database.Services;
using NickERP.Platform.Tenancy;

namespace NickERP.NickFinance.Database;

/// <summary>
/// DI registration helpers for the NickFinance database stack.
///
/// <para>
/// <strong>Optional registration.</strong> Per G2 §11, NickFinance is
/// not required for every deployment — a tenant may go to production
/// without petty-cash. Hosts opt in by calling
/// <see cref="AddNickErpNickFinance"/> when their config carries a
/// <c>ConnectionStrings:NickFinance</c> value; if the value is null or
/// empty, the host should skip the registration entirely.
/// </para>
/// </summary>
public static class NickFinanceDatabaseServiceCollectionExtensions
{
    /// <summary>
    /// Wire <see cref="NickFinanceDbContext"/> + <see cref="IFxRateLookup"/>.
    /// Mirrors how <c>AddNickErpAuditCore</c> registers the audit context
    /// — wires the same tenancy interceptors so RLS plumbing applies
    /// uniformly across modules.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="connectionString">
    /// Postgres connection string for the <c>nickerp_nickfinance</c>
    /// database. Required; throw early rather than letting the first
    /// query fail with a confusing message.
    /// </param>
    public static IServiceCollection AddNickErpNickFinance(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<NickFinanceDbContext>((sp, opts) =>
        {
            opts.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(NickFinanceDbContext).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", NickFinanceDbContext.SchemaName);
            });
            // Mirror inspection: tenancy interceptors push app.tenant_id +
            // app.user_id on every connection open and stamp TenantId on
            // tenant-owned inserts.
            opts.AddInterceptors(
                sp.GetRequiredService<TenantConnectionInterceptor>(),
                sp.GetRequiredService<TenantOwnedEntityInterceptor>());
        });

        services.AddScoped<IFxRateLookup, FxRateLookup>();

        return services;
    }
}
