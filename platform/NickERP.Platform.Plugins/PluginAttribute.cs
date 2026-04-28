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
/// <see cref="Module"/> namespaces the type-code so two modules can ship
/// plugins with the same code without colliding (e.g. NickFinance's
/// <c>momo</c> wallet adapter and a hypothetical NickInspection
/// <c>momo</c> camera adapter coexist as <c>(finance, momo)</c> and
/// <c>(inspection, momo)</c>). Required by G1; existing plugins all set
/// it to <c>"inspection"</c>.
/// </para>
/// <para>
/// The attribute carries no metadata beyond the type code + module.
/// Display name, version, supported formats, JSON-Schema config, etc.
/// live in the sibling <c>plugin.json</c> manifest file (see
/// <see cref="PluginManifest"/>); the manifest's <c>module</c> field
/// must match the attribute.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public string TypeCode { get; }

    /// <summary>
    /// Owning module — namespaces the <see cref="TypeCode"/> so two
    /// modules can ship plugins with the same code without colliding.
    /// Set as a named argument (e.g. <c>[Plugin("momo", Module = "finance")]</c>).
    /// Required; non-empty.
    /// </summary>
    public string Module { get; init; } = string.Empty;

    public PluginAttribute(string typeCode)
    {
        if (string.IsNullOrWhiteSpace(typeCode))
        {
            throw new ArgumentException("Plugin TypeCode must be non-empty.", nameof(typeCode));
        }
        TypeCode = typeCode;
    }
}
