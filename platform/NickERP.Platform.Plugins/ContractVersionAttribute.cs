namespace NickERP.Platform.Plugins;

/// <summary>
/// Marks an Abstractions assembly with a semver-shaped contract version.
/// The plugin loader compares each plugin manifest's
/// <see cref="PluginManifest.MinHostContractVersion"/> against this value
/// and refuses to register the plugin when the host's contract is older
/// than the plugin's declared minimum. Catches stale-DLL drift at startup
/// instead of at first call (e.g. <c>MissingMethodException</c>).
/// </summary>
/// <remarks>
/// Use semver-lite: bump major on a breaking change (record arity, removed
/// field, renamed type), bump minor on an additive change (new optional
/// field). The loader compares as <c>host &gt;= min</c>, so a host at
/// <c>1.5</c> accepts plugins requiring any value in <c>1.0</c>–<c>1.5</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class ContractVersionAttribute : Attribute
{
    public ContractVersionAttribute(string version)
    {
        if (!Version.TryParse(version, out var parsed))
            throw new ArgumentException($"Invalid contract version '{version}'. Use major.minor (e.g. '1.0').", nameof(version));
        Version = parsed;
    }

    public Version Version { get; }
}
