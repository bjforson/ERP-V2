using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Portal.Services.Modules;

/// <summary>
/// Sprint 29 — DI sugar for the portal's module registry. Builds the
/// static catalogue at registration time, binds per-module
/// <c>BaseUrl</c> from <c>Portal:Modules:{Id}:BaseUrl</c> with
/// reasonable dev-default fallbacks, and registers the registry +
/// settings service.
/// </summary>
public static class ModuleRegistryServiceCollectionExtensions
{
    /// <summary>Default base URL for the inspection module on the dev box.</summary>
    public const string DefaultInspectionBaseUrl = "http://localhost:5410";

    /// <summary>Default base URL for the nickfinance G2 module on the dev box.</summary>
    public const string DefaultNickFinanceBaseUrl = "http://localhost:5420";

    /// <summary>Default base URL for the nickhr module on the dev box.</summary>
    public const string DefaultNickHrBaseUrl = "http://localhost:5430";

    /// <summary>
    /// Register the registry + settings service. Catalogue order matches
    /// the registration order — inspection first (the v2-native module),
    /// then NickFinance (G2 + v1-clone coexist), then NickHR (v1-clone).
    /// </summary>
    public static IServiceCollection AddNickErpModuleRegistry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var inspectionUrl = configuration["Portal:Modules:Inspection:BaseUrl"]
            ?? DefaultInspectionBaseUrl;
        var nickFinanceUrl = configuration["Portal:Modules:NickFinance:BaseUrl"]
            ?? DefaultNickFinanceBaseUrl;
        var nickHrUrl = configuration["Portal:Modules:NickHr:BaseUrl"]
            ?? DefaultNickHrBaseUrl;

        var catalogue = new[]
        {
            new ModuleRegistryEntry(
                Id: "inspection",
                DisplayName: "Inspection",
                BaseUrl: inspectionUrl,
                IconHint: "I",
                Description: "Federated inspection — v2-native module",
                Enabled: true,
                Color: "#0ea5e9"),
            new ModuleRegistryEntry(
                Id: "nickfinance",
                DisplayName: "NickFinance",
                BaseUrl: nickFinanceUrl,
                IconHint: "F",
                Description: "Petty cash + GL + AR/AP — G2 pathfinder + v1-clone coexist",
                Enabled: true,
                Color: "#16a34a"),
            new ModuleRegistryEntry(
                Id: "nickhr",
                DisplayName: "NickHR",
                BaseUrl: nickHrUrl,
                IconHint: "H",
                Description: "HR + payroll — v1-clone pending v2-native refactor",
                Enabled: true,
                Color: "#7c3aed"),
        };

        services.TryAddSingleton(new ModuleRegistryOptions { Modules = catalogue });
        services.TryAddScoped<IModuleRegistry, ModuleRegistryService>();
        services.TryAddScoped<ITenantModuleSettingsService, TenantModuleSettingsService>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }
}
