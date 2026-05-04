namespace NickERP.Platform.Web.Shared.Modules;

/// <summary>
/// Sprint 29 — host-configurable bits of the shared chrome. Hosts pass
/// these via <c>builder.Services.AddNickErpSharedChrome(opts =&gt; ...)</c>
/// so the rendered <c>SharedHeader</c> + <c>SharedFooter</c> know which
/// module they're rendering for and where the launcher lives.
/// </summary>
public sealed class NickErpSharedChromeOptions
{
    /// <summary>
    /// Required. The module id this host represents — must match the
    /// <c>Id</c> of the corresponding entry in the portal's
    /// <c>IModuleRegistry</c>. Stable + lowercase (e.g.
    /// <c>"inspection"</c>, <c>"nickfinance"</c>).
    /// </summary>
    public string ModuleId { get; set; } = ModuleContext.DefaultModuleId;

    /// <summary>
    /// Optional human-readable name shown in the chrome's title slot. If
    /// null/empty, <see cref="ModuleContext"/> falls back to a built-in
    /// table for known ids.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional absolute URL of the portal launcher. If null/empty,
    /// <c>SharedHeader</c>'s back-link targets
    /// <see cref="ModuleContext.DefaultLauncherUrl"/>.
    /// </summary>
    public string? PortalLauncherUrl { get; set; }

    /// <summary>
    /// Optional version string surfaced in the footer. If null, the
    /// footer falls back to the module's assembly informational version
    /// at render time.
    /// </summary>
    public string? Version { get; set; }
}
