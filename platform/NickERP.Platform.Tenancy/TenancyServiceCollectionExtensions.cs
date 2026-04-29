using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// DI registration helpers for the NickERP tenancy library. A consuming
/// service registers the in-process tenancy bits with
/// <see cref="AddNickErpTenancy"/> in <c>Program.cs</c> and adds
/// <see cref="UseNickErpTenancy"/> to the request pipeline AFTER
/// <c>UseAuthentication()</c>.
/// </summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITenantContext"/> (scoped per request),
    /// <see cref="IUserContext"/> (scoped per request — Sprint 9 / FU-userid),
    /// <see cref="TenantOwnedEntityInterceptor"/> (scoped — applied via
    /// <c>options.AddInterceptors(...)</c> in module DbContexts), and
    /// <see cref="TenantConnectionInterceptor"/> (scoped — pushes
    /// <c>app.tenant_id</c> + <c>app.user_id</c> to Postgres for RLS).
    /// </summary>
    public static IServiceCollection AddNickErpTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IUserContext, UserContext>();
        services.AddScoped<TenantOwnedEntityInterceptor>();
        services.AddScoped<TenantConnectionInterceptor>();
        return services;
    }

    /// <summary>
    /// Adds <see cref="TenantResolutionMiddleware"/> and
    /// <see cref="UserResolutionMiddleware"/> to the request pipeline.
    /// Place AFTER <c>UseAuthentication()</c> + <c>UseAuthorization()</c>,
    /// BEFORE module endpoints that touch tenant-owned data.
    /// </summary>
    public static IApplicationBuilder UseNickErpTenancy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<TenantResolutionMiddleware>();
        // Sprint 9 / FU-userid — user-context resolution mirrors tenant
        // resolution. Order between the two doesn't matter (independent
        // claims), but both must run before any DbContext usage so the
        // TenantConnectionInterceptor's first ConnectionOpened sees both.
        app.UseMiddleware<UserResolutionMiddleware>();
        return app;
    }
}
