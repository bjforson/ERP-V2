using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// DI registration helpers for the NickERP tenancy library. Modules call
/// <c>services.AddNickERPTenancy()</c> in their <c>Program.cs</c>, then
/// <c>app.UseNickERPTenancy()</c> in the request pipeline (after authentication).
/// </summary>
public static class TenancyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITenantContext"/> (scoped), the entity-stamping
    /// <see cref="TenantOwnedEntityInterceptor"/> (scoped), and the connection-
    /// level <see cref="TenantConnectionInterceptor"/> (scoped) which pushes
    /// the current tenant id down to Postgres so the row-level-security
    /// policies can actually filter. Call this in every module's <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddNickERPTenancy(this IServiceCollection services)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantOwnedEntityInterceptor>();
        services.AddScoped<TenantConnectionInterceptor>();
        return services;
    }

    /// <summary>
    /// Adds the <see cref="TenantResolutionMiddleware"/> to the request pipeline.
    /// Place AFTER <c>UseAuthentication()</c> and BEFORE controllers/endpoints
    /// that touch tenant-owned data.
    /// </summary>
    public static IApplicationBuilder UseNickERPTenancy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
