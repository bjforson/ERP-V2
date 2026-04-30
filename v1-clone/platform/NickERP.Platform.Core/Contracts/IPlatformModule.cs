namespace NickERP.Platform.Core.Contracts;

/// <summary>
/// Every NICKSCAN ERP module implements this interface in its API project's
/// <c>Program.cs</c> as a static module descriptor. The unified ERP portal
/// reads this to populate the App Launcher tile grid and to verify the module
/// satisfies the platform contract version it ships against.
/// </summary>
public interface IPlatformModule
{
    /// <summary>Short kebab-case identifier (e.g. "nscis", "nickhr", "finance").</summary>
    string Name { get; }

    /// <summary>Human-readable name shown in the App Launcher (e.g. "NickScan Central Imaging").</summary>
    string DisplayName { get; }

    /// <summary>Semver version of the module.</summary>
    string Version { get; }

    /// <summary>One-line description shown on the App Launcher tile.</summary>
    string Description { get; }

    /// <summary>The platform contract version this module targets (see <c>PLATFORM.md</c>).</summary>
    string PlatformContractVersion { get; }

    /// <summary>Material icon name or asset path for the App Launcher tile.</summary>
    string Icon { get; }

    /// <summary>Brand color for the tile (CSS hex string).</summary>
    string Color { get; }

    /// <summary>Default landing route after the user clicks the tile.</summary>
    string HomeRoute { get; }

    /// <summary>
    /// Platform service dependencies this module requires. The portal warns
    /// if a tenant has the module enabled but is missing one of these.
    /// Use lower-case names: "comms", "files", "audit", "notify", "workflow",
    /// "tenancy", "identity", "jobs", "reporting".
    /// </summary>
    IReadOnlyList<string> PlatformDependencies { get; }

    /// <summary>
    /// Capability tokens advertised by this module (used for cross-module
    /// integrations and feature gating). Free-form, dot-separated.
    /// e.g. "container.scan", "payroll.run", "invoice.post".
    /// </summary>
    IReadOnlyList<string> Capabilities { get; }
}
