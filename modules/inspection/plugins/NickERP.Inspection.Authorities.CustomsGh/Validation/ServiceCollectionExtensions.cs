using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Authorities.CustomsGh.Validation;

/// <summary>
/// Sprint 28 / B4 — DI helper for tests + alternative hosts that take a
/// project reference on this assembly. The production host
/// (<c>Inspection.Web</c>) loads the plugin DLL through
/// <c>AddNickErpPluginsEager</c> + <c>PluginValidationRuleRegistration</c>,
/// which auto-registers the rules without going through this entry point.
///
/// <para>
/// Test fixtures use <see cref="AddCustomsGhValidation"/> directly so
/// they don't have to drop a built DLL into a plugins folder during
/// the test run.
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="CustomsGhValidationOptions"/> from the
    /// <c>CustomsGhValidation</c> configuration section + register the
    /// three rules as <see cref="IValidationRule"/> contributions.
    /// Idempotent (TryAddEnumerable).
    /// </summary>
    public static IServiceCollection AddCustomsGhValidation(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
        {
            services.AddOptions<CustomsGhValidationOptions>()
                .Bind(configuration.GetSection(CustomsGhValidationOptions.SectionName));
        }
        else
        {
            services.AddOptions<CustomsGhValidationOptions>();
        }

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidationRule, PortMatchRule>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidationRule, FycoDirectionRule>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IValidationRule, CmrPortRule>());

        return services;
    }
}
