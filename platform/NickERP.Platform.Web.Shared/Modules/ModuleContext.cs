namespace NickERP.Platform.Web.Shared.Modules;

/// <summary>
/// Sprint 29 — default <see cref="IModuleContext"/> implementation. Plain
/// record so DI can construct it from the options the host configures via
/// <see cref="NickErpSharedChromeOptions"/>; no per-request state.
/// </summary>
public sealed class ModuleContext : IModuleContext
{
    /// <summary>
    /// Construct the context. Empty / null inputs fall back to safe
    /// defaults so a misconfigured host still renders chrome instead of
    /// throwing during component initialization.
    /// </summary>
    public ModuleContext(string? moduleId, string? displayName, string? portalLauncherUrl)
    {
        ModuleId = string.IsNullOrWhiteSpace(moduleId) ? DefaultModuleId : moduleId;
        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? FallbackDisplayName(ModuleId)
            : displayName;
        PortalLauncherUrl = string.IsNullOrWhiteSpace(portalLauncherUrl)
            ? DefaultLauncherUrl
            : portalLauncherUrl;
    }

    /// <inheritdoc />
    public string ModuleId { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public string PortalLauncherUrl { get; }

    /// <summary>Sentinel id for hosts that don't declare a module.</summary>
    public const string DefaultModuleId = "portal";

    /// <summary>
    /// Default v2 portal URL. Matches the value in
    /// <c>AppSwitcher.DefaultApps[portal].Href</c> so a host without
    /// explicit <c>Portal:LauncherUrl</c> still lands on the right place
    /// for the dev box.
    /// </summary>
    public const string DefaultLauncherUrl = "http://localhost:5400/";

    private static string FallbackDisplayName(string moduleId) => moduleId switch
    {
        "inspection" => "Inspection",
        "nickfinance" => "NickFinance",
        "nickhr" => "NickHR",
        "portal" => "NickERP Portal",
        _ => moduleId,
    };
}
