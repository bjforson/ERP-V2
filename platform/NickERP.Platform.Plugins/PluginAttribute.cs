namespace NickERP.Platform.Plugins;

/// <summary>
/// Marks a class as a NickERP platform plugin. The
/// <see cref="PluginLoader"/> scans assemblies in the configured plugins
/// directory for any concrete class decorated with this attribute and
/// registers an instance keyed by <see cref="TypeCode"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TypeCode"/> is the stable identifier the rest of the system
/// references the plugin by — it ends up persisted in module config and
/// runtime tables. Pick a kebab-case identifier (e.g. <c>fs6000</c>,
/// <c>icums-gh</c>, <c>customs-gh</c>); changing it later requires a
/// migration sweep across every module that stored it.
/// </para>
/// <para>
/// The attribute carries no metadata beyond the type code. Display name,
/// version, supported formats, JSON-Schema config, etc. live in the
/// sibling <c>plugin.json</c> manifest file (see <see cref="PluginManifest"/>).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public string TypeCode { get; }

    public PluginAttribute(string typeCode)
    {
        if (string.IsNullOrWhiteSpace(typeCode))
        {
            throw new ArgumentException("Plugin TypeCode must be non-empty.", nameof(typeCode));
        }
        TypeCode = typeCode;
    }
}
