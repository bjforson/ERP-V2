namespace NickERP.Platform.Core;

/// <summary>
/// The current version of the NICKSCAN ERP Platform Contract that this
/// shared library publishes. Modules echo this value in their manifest
/// (<c>ModuleManifest.PlatformContractVersion</c>) so the portal can
/// detect mismatches.
///
/// Bump the major version when a breaking change to <c>PLATFORM.md</c> ships.
/// Bump the minor version for additive changes.
/// </summary>
public static class PlatformContractVersion
{
    public const string Current = "1.0";
}
