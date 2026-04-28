namespace NickERP.Platform.Plugins;

/// <summary>
/// One row in the <see cref="IPluginRegistry"/> per concrete plugin class.
/// Carries the resolved type, the manifest, and a list of implemented
/// contract types so the registry can answer "give me everything that
/// implements <c>IScannerAdapter</c>" without reflection at lookup time.
/// </summary>
/// <param name="Module">G1 #5 — the owning module ("inspection", "finance", ...). Lookups are keyed on (Module, TypeCode).</param>
/// <param name="TypeCode">Plugin type code, kebab-case (e.g. <c>fs6000</c>, <c>icums-gh</c>). Unique within a module.</param>
/// <param name="ConcreteType">The plugin's concrete <see cref="Type"/> — what the registry instantiates via DI.</param>
/// <param name="ContractTypes">Interfaces the concrete type implements; used by <see cref="IPluginRegistry.ForContract"/>.</param>
/// <param name="Manifest">Sidecar <c>plugin.json</c> metadata loaded alongside the assembly.</param>
public sealed record RegisteredPlugin(
    string Module,
    string TypeCode,
    Type ConcreteType,
    IReadOnlyList<Type> ContractTypes,
    PluginManifest Manifest);
