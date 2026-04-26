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
    /// <see cref="TenantOwnedEntityInterceptor"/> (scoped — applied via
    /// <c>options.AddInterceptors(...)</c> in module DbContexts), and
    /// <see cref="TenantConnectionInterceptor"/> (scoped — pushes
    /// <c>app.tenant_id</c> to Postgres for RLS).
    /// </summary>
    public static IServiceCollection AddNickErpTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantOwnedEntityInterceptor>();
        services.AddScoped<TenantConnectionInterceptor>();
        return services;
    }

    /// <summary>
    /// Adds <see cref="TenantResolutionMiddleware"/> to the request
    /// pipeline. Place AFTER <c>UseAuthentication()</c> + <c>UseAuthorization()</c>,
    /// BEFORE module endpoints that touch tenant-owned data.
    /// </summary>
    public static IApplicationBuilder UseNickErpTenancy(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
