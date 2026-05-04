namespace NickERP.Platform.Web.Shared.Modules;

/// <summary>
/// Sprint 29 — per-request "which module is this app?" context. Resolved
/// once per host (singleton) from <c>NickErpSharedChromeOptions.ModuleId</c>
/// + the portal launcher URL the host configures. Consumed by
/// <c>SharedHeader</c> + <c>SharedFooter</c> so the chrome always knows
/// which module label to render and where to send the back-to-launcher
/// link.
/// </summary>
public interface IModuleContext
{
    /// <summary>
    /// Stable id of the current module — matches the
    /// <c>ModuleRegistryEntry.Id</c> emitted by the portal-side registry
    /// (e.g. <c>"inspection"</c>, <c>"nickfinance"</c>, <c>"nickhr"</c>).
    /// Hosts that don't register a module (e.g. the portal itself when it
    /// renders chrome only, or unknown bootstrapping paths) return
    /// <c>"portal"</c> as a sentinel.
    /// </summary>
    string ModuleId { get; }

    /// <summary>
    /// Human-readable display name for the current module — e.g.
    /// <c>"Inspection"</c>, <c>"NickFinance"</c>. Used in the shared
    /// header's title bar and the &lt;title&gt; element on module hosts.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Absolute URL of the portal launcher (e.g.
    /// <c>"http://localhost:5400/"</c>). The "back to launcher" link in
    /// <c>SharedHeader</c> targets this. Hosts configure it via
    /// <c>Portal:LauncherUrl</c>; default falls back to
    /// <c>http://localhost:5400/</c>.
    /// </summary>
    string PortalLauncherUrl { get; }
}
