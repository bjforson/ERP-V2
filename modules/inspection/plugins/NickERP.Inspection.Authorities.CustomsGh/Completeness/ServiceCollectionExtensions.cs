using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NickERP.Inspection.Core.Completeness;

namespace NickERP.Inspection.Authorities.CustomsGh.Completeness;

/// <summary>
/// Sprint 48 / Phase C — DI helpers for the CustomsGh completeness
/// requirements.
///
/// <para>
/// Mirrors <see cref="Validation.ServiceCollectionExtensions.AddCustomsGhValidation"/>
/// in shape — the production host (<c>Inspection.Web</c>) loads the
/// plugin DLL through <c>AddNickErpPluginsEager</c> and the registration
/// pass discovers the requirements automatically; tests use this entry
/// point directly so they don't need a built DLL.
/// </para>
///
/// <para>
/// Idempotent (TryAddEnumerable). Binds <see cref="CustomsGhValidationOptions"/>
/// when a configuration section is supplied so the rules can read the
/// transit-regime list (shared with the validation rules).
/// </para>
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the CustomsGh-specific completeness requirements + bind
    /// <see cref="CustomsGhValidationOptions"/> from the
    /// <c>CustomsGhValidation</c> configuration section. Idempotent.
    /// </summary>
    public static IServiceCollection AddCustomsGhCompleteness(
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

        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICompletenessRequirement, CmrPortStateRequirement>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ICompletenessRequirement, RegimeSpecificDocumentsRequirement>());

        return services;
    }
}
