using Microsoft.Extensions.DependencyInjection;

namespace NickERP.Platform.Plugins;

/// <summary>
/// Singleton registry of every plugin discovered at startup. Modules query
/// it to enumerate available plugins (e.g. for admin-UI dropdowns) and to
/// resolve a specific plugin by (<see cref="RegisteredPlugin.Module"/>,
/// <see cref="RegisteredPlugin.TypeCode"/>).
/// </summary>
/// <remarks>
/// G1 #5 — lookups are namespaced by <see cref="RegisteredPlugin.Module"/>
/// so two modules can ship plugins with the same TypeCode without
/// collision. Existing call sites pass <c>"inspection"</c> as the module.
/// </remarks>
public interface IPluginRegistry
{
    /// <summary>Every plugin discovered at startup.</summary>
    IReadOnlyList<RegisteredPlugin> All { get; }

    /// <summary>Filter to plugins implementing a specific contract type (e.g. <c>typeof(IScannerAdapter)</c>).</summary>
    IReadOnlyList<RegisteredPlugin> ForContract(Type contractType);

    /// <summary>
    /// Look up a plugin by (<paramref name="module"/>, <paramref name="typeCode"/>).
    /// Returns <see langword="null"/> if not registered.
    /// </summary>
    RegisteredPlugin? FindByTypeCode(string module, string typeCode);

    /// <summary>
    /// Resolve an instance of the plugin via DI. Throws if the plugin is not
    /// registered (in <paramref name="module"/>) or doesn't implement the
    /// requested contract.
    /// </summary>
    T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class;
}

/// <summary>Default <see cref="IPluginRegistry"/> backed by an in-memory dictionary.</summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly IReadOnlyList<RegisteredPlugin> _all;
    private readonly IReadOnlyDictionary<(string Module, string TypeCode), RegisteredPlugin> _byKey;

    public PluginRegistry(IEnumerable<RegisteredPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        _all = plugins.ToList();
        _byKey = _all.ToDictionary(
            p => (p.Module.ToLowerInvariant(), p.TypeCode.ToLowerInvariant()),
            ModuleTypeCodeComparer.Instance);
    }

    public IReadOnlyList<RegisteredPlugin> All => _all;

    public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return _all.Where(p => p.ContractTypes.Contains(contractType)).ToList();
    }

    public RegisteredPlugin? FindByTypeCode(string module, string typeCode)
    {
        if (string.IsNullOrWhiteSpace(module)) return null;
        if (string.IsNullOrWhiteSpace(typeCode)) return null;
        return _byKey.TryGetValue(
            (module.ToLowerInvariant(), typeCode.ToLowerInvariant()),
            out var p) ? p : null;
    }

    public T Resolve<T>(string module, string typeCode, IServiceProvider services) where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = FindByTypeCode(module, typeCode)
            ?? throw new KeyNotFoundException($"No plugin registered with (Module='{module}', TypeCode='{typeCode}').");

        if (!registered.ContractTypes.Contains(typeof(T)))
        {
            throw new InvalidOperationException(
                $"Plugin ({module}, {typeCode}) does not implement {typeof(T).FullName}. "
                + $"Implements: {string.Join(", ", registered.ContractTypes.Select(c => c.FullName))}.");
        }

        return (T)services.GetRequiredService(registered.ConcreteType);
    }

    private sealed class ModuleTypeCodeComparer : IEqualityComparer<(string Module, string TypeCode)>
    {
        public static readonly ModuleTypeCodeComparer Instance = new();

        public bool Equals((string Module, string TypeCode) x, (string Module, string TypeCode) y) =>
            string.Equals(x.Module, y.Module, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.TypeCode, y.TypeCode, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Module, string TypeCode) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Module ?? string.Empty),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TypeCode ?? string.Empty));
    }
}
