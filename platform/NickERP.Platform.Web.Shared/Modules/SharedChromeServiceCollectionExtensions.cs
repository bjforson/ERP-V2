using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NickERP.Platform.Web.Shared.Modules;

/// <summary>
/// Sprint 29 — DI sugar so module hosts wire shared chrome with one call:
/// <code>
/// builder.Services.AddNickErpSharedChrome(opts =&gt;
/// {
///     opts.ModuleId = "inspection";
///     opts.DisplayName = "Inspection v2";
///     opts.PortalLauncherUrl = builder.Configuration["Portal:LauncherUrl"];
/// });
/// </code>
/// Idempotent (TryAddSingleton); a host that calls it twice still gets a
/// single context. The portal itself does NOT need to call this — its
/// <see cref="IModuleContext"/> binding lives alongside the registry.
/// </summary>
public static class SharedChromeServiceCollectionExtensions
{
    /// <summary>
    /// Register <see cref="IModuleContext"/> using the supplied
    /// configuration delegate. Returns the option instance so callers can
    /// chain assertions in tests.
    /// </summary>
    public static NickErpSharedChromeOptions AddNickErpSharedChrome(
        this IServiceCollection services,
        Action<NickErpSharedChromeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new NickErpSharedChromeOptions();
        configure(options);

        services.TryAddSingleton<IModuleContext>(_ => new ModuleContext(
            options.ModuleId,
            options.DisplayName,
            options.PortalLauncherUrl));

        // Expose the raw options for the footer's version slot. Singleton
        // matches the context lifetime; both are read-only after startup.
        services.TryAddSingleton(options);

        return options;
    }
}
