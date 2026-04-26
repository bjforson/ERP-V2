namespace NickERP.Platform.Plugins;

/// <summary>
/// One row in the <see cref="IPluginRegistry"/> per concrete plugin class.
/// Carries the resolved type, the manifest, and a list of implemented
/// contract types so the registry can answer "give me everything that
/// implements <c>IScannerAdapter</c>" without reflection at lookup time.
/// </summary>
public sealed record RegisteredPlugin(
    string TypeCode,
    Type ConcreteType,
    IReadOnlyList<Type> ContractTypes,
    PluginManifest Manifest);
