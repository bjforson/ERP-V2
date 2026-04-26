using System.Reflection;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Plugins;

/// <summary>
/// Scans a directory for plugin assemblies, loads them, and produces a
/// <see cref="RegisteredPlugin"/> per <see cref="PluginAttribute"/>-decorated
/// class. Singleton — runs once at startup; the resulting list seeds the
/// <see cref="IPluginRegistry"/>.
/// </summary>
public sealed class PluginLoader
{
    private readonly ILogger<PluginLoader> _logger;

    public PluginLoader(ILogger<PluginLoader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Scan <paramref name="pluginsDirectory"/> for <c>*.dll</c> files paired
    /// with <c>plugin.json</c> sidecar manifests. Each manifest's
    /// <see cref="PluginManifest.TypeCode"/> must match a class in its
    /// assembly decorated with <c>[Plugin(typeCode)]</c>; mismatches are
    /// logged and skipped.
    /// </summary>
    /// <param name="pluginsDirectory">Absolute path to the plugins folder. Returns empty if it doesn't exist.</param>
    /// <returns>Discovered plugins. Order is filesystem-enumeration order, not stable across runs.</returns>
    public IReadOnlyList<RegisteredPlugin> LoadFrom(string pluginsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        if (!Directory.Exists(pluginsDirectory))
        {
            _logger.LogInformation("Plugins directory does not exist: {Dir}. No plugins loaded.", pluginsDirectory);
            return Array.Empty<RegisteredPlugin>();
        }

        var found = new List<RegisteredPlugin>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dllPath in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var manifestPath = Path.Combine(pluginsDirectory, Path.GetFileNameWithoutExtension(dllPath) + "." + PluginManifest.FileName);
            // Also try a generic plugin.json sibling — some adapters ship one manifest for a single dll.
            if (!File.Exists(manifestPath))
            {
                manifestPath = Path.Combine(pluginsDirectory, PluginManifest.FileName);
                if (!File.Exists(manifestPath))
                {
                    _logger.LogWarning("Skipping {Dll}: no manifest sidecar (looked for {ManifestA} and {ManifestB}).",
                        dllPath, dllPath + "." + PluginManifest.FileName, manifestPath);
                    continue;
                }
            }

            PluginManifest manifest;
            try
            {
                manifest = PluginManifest.LoadFrom(manifestPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin manifest at {Path}; skipping.", manifestPath);
                continue;
            }

            if (!seenCodes.Add(manifest.TypeCode))
            {
                _logger.LogError("Duplicate plugin TypeCode '{TypeCode}' (manifest {Path}); skipping. The first registration wins.", manifest.TypeCode, manifestPath);
                continue;
            }

            Assembly asm;
            try
            {
                asm = Assembly.LoadFrom(dllPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin assembly {Dll}; skipping.", dllPath);
                continue;
            }

            var match = ExtractPluginType(asm, manifest.TypeCode);
            if (match is null)
            {
                _logger.LogError(
                    "Plugin manifest {Path} declares TypeCode '{TypeCode}' but no [Plugin(\"{TypeCode}\")]-decorated class was found in {Assembly}.",
                    manifestPath, manifest.TypeCode, manifest.TypeCode, asm.FullName ?? dllPath);
                continue;
            }

            var contracts = match.GetInterfaces();
            found.Add(new RegisteredPlugin(manifest.TypeCode, match, contracts, manifest));
            _logger.LogInformation(
                "Loaded plugin {TypeCode} ({DisplayName}, v{Version}) from {Dll}; implements {ContractCount} contract(s).",
                manifest.TypeCode, manifest.DisplayName, manifest.Version, dllPath, contracts.Length);
        }

        return found;
    }

    /// <summary>
    /// Discover plugins from in-process loaded assemblies (rather than scanning a folder).
    /// Useful when a host bundles plugins as project references for tests / dev builds.
    /// </summary>
    public IReadOnlyList<RegisteredPlugin> LoadFromAssemblies(IEnumerable<Assembly> assemblies, IReadOnlyDictionary<string, PluginManifest> manifestsByTypeCode)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(manifestsByTypeCode);

        var found = new List<RegisteredPlugin>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asm in assemblies)
        {
            foreach (var (type, attr) in EnumeratePluginTypes(asm))
            {
                if (!seen.Add(attr.TypeCode))
                {
                    _logger.LogError("Duplicate plugin TypeCode '{TypeCode}' across assemblies; skipping subsequent registrations.", attr.TypeCode);
                    continue;
                }
                if (!manifestsByTypeCode.TryGetValue(attr.TypeCode, out var manifest))
                {
                    _logger.LogError("[Plugin(\"{TypeCode}\")] type {Type} has no matching manifest entry; skipping.", attr.TypeCode, type.FullName);
                    continue;
                }
                var contracts = type.GetInterfaces();
                found.Add(new RegisteredPlugin(attr.TypeCode, type, contracts, manifest));
            }
        }

        return found;
    }

    private static Type? ExtractPluginType(Assembly asm, string typeCode)
    {
        foreach (var (type, attr) in EnumeratePluginTypes(asm))
        {
            if (string.Equals(attr.TypeCode, typeCode, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }
        return null;
    }

    private static IEnumerable<(Type Type, PluginAttribute Attr)> EnumeratePluginTypes(Assembly asm)
    {
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types failed to load (missing transitive deps). Use the partial list.
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface) continue;
            var attr = type.GetCustomAttribute<PluginAttribute>(inherit: false);
            if (attr is null) continue;
            yield return (type, attr);
        }
    }
}
