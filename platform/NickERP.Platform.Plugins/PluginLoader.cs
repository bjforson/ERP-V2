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
        // G1 #5 — keys are (module, typeCode), so two modules can ship the
        // same typeCode. Lower-cased for case-insensitive uniqueness.
        var seenKeys = new HashSet<(string Module, string TypeCode)>();

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

            var dedupKey = (
                Module: (manifest.Module ?? string.Empty).ToLowerInvariant(),
                TypeCode: manifest.TypeCode.ToLowerInvariant());
            if (!seenKeys.Add(dedupKey))
            {
                _logger.LogError(
                    "Duplicate plugin (Module='{Module}', TypeCode='{TypeCode}') (manifest {Path}); skipping. The first registration wins.",
                    manifest.Module, manifest.TypeCode, manifestPath);
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

            var match = ExtractPluginType(asm, manifest.TypeCode, manifest.Module ?? string.Empty);
            if (match is null)
            {
                _logger.LogError(
                    "Plugin manifest {Path} declares (Module='{Module}', TypeCode='{TypeCode}') but no matching [Plugin(...)]-decorated class was found in {Assembly}.",
                    manifestPath, manifest.Module, manifest.TypeCode, asm.FullName ?? dllPath);
                continue;
            }

            if (!CheckContractVersions(manifest))
            {
                continue;
            }

            var contracts = match.GetInterfaces();
            found.Add(new RegisteredPlugin(manifest.Module ?? string.Empty, manifest.TypeCode, match, contracts, manifest));
            _logger.LogInformation(
                "Loaded plugin ({Module}, {TypeCode}) ({DisplayName}, v{Version}) from {Dll}; implements {ContractCount} contract(s).",
                manifest.Module, manifest.TypeCode, manifest.DisplayName, manifest.Version, dllPath, contracts.Length);
        }

        return found;
    }

    /// <summary>
    /// Validate the plugin's <see cref="PluginManifest.MinHostContractVersion"/>
    /// against the host's <see cref="ContractVersionAttribute"/>-stamped
    /// Abstractions assemblies for each contract listed in the manifest.
    /// Returns <see langword="true"/> if the plugin should be registered,
    /// <see langword="false"/> if it should be skipped (with the reason
    /// already logged at Error level).
    /// </summary>
    private bool CheckContractVersions(PluginManifest manifest)
    {
        // No declared minimum → legacy plugin; skip the check (loader is
        // permissive by default to keep older plugins loading).
        if (string.IsNullOrWhiteSpace(manifest.MinHostContractVersion))
        {
            return true;
        }

        if (!Version.TryParse(manifest.MinHostContractVersion, out var minVersion))
        {
            _logger.LogError(
                "Plugin '{TypeCode}' has invalid minHostContractVersion '{Min}' (expected major.minor); skipping.",
                manifest.TypeCode, manifest.MinHostContractVersion);
            return false;
        }

        // Validate against every contract the manifest declares. If any
        // contract type is reachable and stamped with [ContractVersion]
        // older than the plugin's minimum, reject. Unstamped contract
        // assemblies fall back to 1.0 with a single warning per check.
        if (manifest.Contracts is null || manifest.Contracts.Count == 0)
        {
            // No contracts declared — nothing to compare against. Treat as
            // pass; the [Plugin] attribute scan will catch unusable types.
            return true;
        }

        foreach (var contractTypeName in manifest.Contracts)
        {
            if (string.IsNullOrWhiteSpace(contractTypeName)) continue;

            var contractType = ResolveContractType(contractTypeName);
            if (contractType is null)
            {
                // Type couldn't be resolved from any loaded assembly — let
                // the [Plugin] interface scan handle this; don't fail the
                // version check on a missing contract.
                _logger.LogWarning(
                    "Plugin '{TypeCode}' declares contract '{Contract}' but the type could not be resolved at version-check time; assuming 1.0.",
                    manifest.TypeCode, contractTypeName);
                continue;
            }

            var hostVersionAttr = contractType.Assembly.GetCustomAttribute<ContractVersionAttribute>();
            Version hostVersion;
            if (hostVersionAttr is null)
            {
                _logger.LogWarning(
                    "Contract assembly for '{Contract}' has no [ContractVersion]; assuming 1.0 for plugin '{TypeCode}'.",
                    contractType.FullName ?? contractType.Name, manifest.TypeCode);
                hostVersion = new Version(1, 0);
            }
            else
            {
                hostVersion = hostVersionAttr.Version;
            }

            if (hostVersion < minVersion)
            {
                _logger.LogError(
                    "Plugin '{TypeCode}' requires {Contract}@{Min} but host has @{Host}; skipping.",
                    manifest.TypeCode, contractType.Name, minVersion, hostVersion);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolve a contract type by its fully-qualified name from any
    /// currently-loaded assembly. The contract Abstractions assemblies are
    /// already loaded by the host before <see cref="LoadFrom"/> runs (the
    /// host project-references them), so a simple
    /// <see cref="AppDomain.GetAssemblies"/> sweep is sufficient.
    /// </summary>
    private static Type? ResolveContractType(string contractTypeName)
    {
        // Try the fast path first.
        var t = Type.GetType(contractTypeName);
        if (t is not null) return t;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                t = asm.GetType(contractTypeName, throwOnError: false, ignoreCase: false);
                if (t is not null) return t;
            }
            catch
            {
                // Some dynamic assemblies (Razor compiled views, etc.) throw
                // here — ignore and keep searching.
            }
        }
        return null;
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
        var seen = new HashSet<(string Module, string TypeCode)>();

        foreach (var asm in assemblies)
        {
            foreach (var (type, attr) in EnumeratePluginTypes(asm))
            {
                if (string.IsNullOrWhiteSpace(attr.Module))
                {
                    _logger.LogError(
                        "[Plugin(\"{TypeCode}\")] type {Type} has no Module set; required by G1 #5. Skipping.",
                        attr.TypeCode, type.FullName);
                    continue;
                }
                var key = (attr.Module.ToLowerInvariant(), attr.TypeCode.ToLowerInvariant());
                if (!seen.Add(key))
                {
                    _logger.LogError(
                        "Duplicate plugin (Module='{Module}', TypeCode='{TypeCode}') across assemblies; skipping subsequent registrations.",
                        attr.Module, attr.TypeCode);
                    continue;
                }
                if (!manifestsByTypeCode.TryGetValue(attr.TypeCode, out var manifest))
                {
                    _logger.LogError("[Plugin(\"{TypeCode}\")] type {Type} has no matching manifest entry; skipping.", attr.TypeCode, type.FullName);
                    continue;
                }
                if (!string.Equals(manifest.Module, attr.Module, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError(
                        "[Plugin(\"{TypeCode}\", Module=\"{AttrModule}\")] disagrees with manifest module '{ManifestModule}' for type {Type}; skipping.",
                        attr.TypeCode, attr.Module, manifest.Module, type.FullName);
                    continue;
                }
                if (!CheckContractVersions(manifest))
                {
                    continue;
                }
                var contracts = type.GetInterfaces();
                found.Add(new RegisteredPlugin(attr.Module, attr.TypeCode, type, contracts, manifest));
            }
        }

        return found;
    }

    private static Type? ExtractPluginType(Assembly asm, string typeCode, string module)
    {
        foreach (var (type, attr) in EnumeratePluginTypes(asm))
        {
            if (string.Equals(attr.TypeCode, typeCode, StringComparison.OrdinalIgnoreCase)
                && string.Equals(attr.Module, module, StringComparison.OrdinalIgnoreCase))
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
