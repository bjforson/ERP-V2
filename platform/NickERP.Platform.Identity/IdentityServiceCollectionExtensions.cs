using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Identity.Auth;

namespace NickERP.Platform.Identity;

/// <summary>
/// DI registration helpers for the NickERP identity layer. A consuming
/// service typically calls these in two complementary ways:
/// <list type="number">
///   <item><description><see cref="AddNickErpIdentity"/> on this static class — registers <see cref="ICfJwtValidator"/>, binds <see cref="CfAccessAuthenticationOptions"/>, registers the auth scheme.</description></item>
///   <item><description><c>AddNickErpIdentityCore</c> on <c>NickERP.Platform.Identity.Database</c> — registers <c>IdentityDbContext</c> + the DB-backed <see cref="Services.IIdentityResolver"/>.</description></item>
/// </list>
/// Both calls are required for a working setup; they're separated so a
/// consumer can swap the resolver implementation (e.g. an HTTP-client
/// resolver later) without re-registering the auth scheme.
/// </summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Wire CF Access JWT validation + the NickERP authentication scheme.
    /// Reads <c>NickErp:Identity:CfAccess</c> from configuration. Throws at
    /// startup if required fields (TeamDomain, ApplicationAudience) are
    /// missing OR if dev bypass is enabled outside the
    /// <c>Development</c> environment.
    /// </summary>
    /// <returns>The <see cref="AuthenticationBuilder"/> so consumers can chain additional schemes.</returns>
    public static AuthenticationBuilder AddNickErpIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new CfAccessAuthenticationOptions();
        configuration.GetSection(CfAccessAuthenticationOptions.SectionName).Bind(options);
        options.Validate();

        if (options.DevBypass.Enabled && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{CfAccessAuthenticationOptions.SectionName}:DevBypass:Enabled is true outside the "
                + $"Development environment ({environment.EnvironmentName}). This is a security regression — "
                + "set DevBypass:Enabled=false in production configuration.");
        }

        services.AddSingleton(options);
        services.AddSingleton<ICfJwtValidator, CfJwtValidator>();

        return services.AddAuthentication(CfAccessAuthenticationOptions.SchemeName)
            .AddScheme<NickErpAuthenticationHandler.SchemeOptions, NickErpAuthenticationHandler>(
                CfAccessAuthenticationOptions.SchemeName,
                _ => { });
    }
}
