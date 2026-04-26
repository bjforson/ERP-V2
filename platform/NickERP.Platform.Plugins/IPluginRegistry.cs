using Microsoft.Extensions.DependencyInjection;

namespace NickERP.Platform.Plugins;

/// <summary>
/// Singleton registry of every plugin discovered at startup. Modules query
/// it to enumerate available plugins (e.g. for admin-UI dropdowns) and to
/// resolve a specific plugin by <see cref="RegisteredPlugin.TypeCode"/>.
/// </summary>
public interface IPluginRegistry
{
    /// <summary>Every plugin discovered at startup.</summary>
    IReadOnlyList<RegisteredPlugin> All { get; }

    /// <summary>Filter to plugins implementing a specific contract type (e.g. <c>typeof(IScannerAdapter)</c>).</summary>
    IReadOnlyList<RegisteredPlugin> ForContract(Type contractType);

    /// <summary>Look up a plugin by its <see cref="PluginAttribute.TypeCode"/>. Returns <see langword="null"/> if not registered.</summary>
    RegisteredPlugin? FindByTypeCode(string typeCode);

    /// <summary>
    /// Resolve an instance of the plugin via DI. Throws if the plugin is not
    /// registered or doesn't implement the requested contract.
    /// </summary>
    T Resolve<T>(string typeCode, IServiceProvider services) where T : class;
}

/// <summary>Default <see cref="IPluginRegistry"/> backed by an in-memory dictionary.</summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly IReadOnlyList<RegisteredPlugin> _all;
    private readonly IReadOnlyDictionary<string, RegisteredPlugin> _byTypeCode;

    public PluginRegistry(IEnumerable<RegisteredPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        _all = plugins.ToList();
        _byTypeCode = _all.ToDictionary(p => p.TypeCode, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RegisteredPlugin> All => _all;

    public IReadOnlyList<RegisteredPlugin> ForContract(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return _all.Where(p => p.ContractTypes.Contains(contractType)).ToList();
    }

    public RegisteredPlugin? FindByTypeCode(string typeCode)
    {
        if (string.IsNullOrWhiteSpace(typeCode)) return null;
        return _byTypeCode.TryGetValue(typeCode, out var p) ? p : null;
    }

    public T Resolve<T>(string typeCode, IServiceProvider services) where T : class
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = FindByTypeCode(typeCode)
            ?? throw new KeyNotFoundException($"No plugin registered with TypeCode '{typeCode}'.");

        if (!registered.ContractTypes.Contains(typeof(T)))
        {
            throw new InvalidOperationException(
                $"Plugin '{typeCode}' does not implement {typeof(T).FullName}. "
                + $"Implements: {string.Join(", ", registered.ContractTypes.Select(c => c.FullName))}.");
        }

        return (T)services.GetRequiredService(registered.ConcreteType);
    }
}
