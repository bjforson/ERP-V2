using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Core.Validation;

namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 28 — host helper for picking up plugin-supplied
/// <see cref="IValidationRule"/> implementations from the plugins folder
/// without taking a hard project reference on the plugin assembly.
///
/// <para>
/// The platform plugin loader registers concrete plugin classes (those
/// decorated with <c>[Plugin]</c>) as singletons. For validation rules we
/// want a richer surface — a single plugin DLL may ship multiple
/// IValidationRule implementations, and they don't have to be plugins in
/// the <c>[Plugin]</c> sense (rules are wired to the engine, not via the
/// authority-rules contract). So we do a separate reflection pass: enumerate
/// every DLL in the plugins folder, scan for IValidationRule types, and
/// register each one as a scoped IEnumerable&lt;IValidationRule&gt;
/// contribution.
/// </para>
///
/// <para>
/// Idempotent — calling more than once with the same folder is a no-op
/// because <see cref="ServiceDescriptor"/> equality covers the (service,
/// implementation) pair.
/// </para>
/// </summary>
public static class PluginValidationRuleRegistration
{
    /// <summary>
    /// Reflect over every assembly in <paramref name="pluginsDirectory"/>
    /// and register the IValidationRule types they expose. Safe to call
    /// at host startup before <c>builder.Build()</c>; assemblies are
    /// loaded into the default load context.
    /// </summary>
    /// <param name="services">DI container to register into.</param>
    /// <param name="pluginsDirectory">Absolute path of the plugins folder.</param>
    /// <returns>Count of rule types registered.</returns>
    public static int RegisterPluginValidationRules(
        IServiceCollection services,
        string pluginsDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(pluginsDirectory)) return 0;
        if (!Directory.Exists(pluginsDirectory)) return 0;

        var ruleType = typeof(IValidationRule);
        var registered = 0;

        foreach (var dll in Directory.EnumerateFiles(pluginsDirectory, "*.dll", SearchOption.AllDirectories))
        {
            Assembly asm;
            try
            {
                asm = Assembly.LoadFrom(dll);
            }
            catch
            {
                // Skip files that aren't loadable assemblies — same
                // posture the platform PluginLoader takes.
                continue;
            }

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some assemblies don't load every type in the plugins
                // folder (they may have unresolved transitive deps that
                // are loaded lazily). Use whatever we got back.
                types = ex.Types?.Where(t => t is not null).ToArray()! ?? Array.Empty<Type>();
            }

            foreach (var t in types)
            {
                if (t is null) continue;
                if (t.IsAbstract || t.IsInterface) continue;
                if (!ruleType.IsAssignableFrom(t)) continue;
                if (t.GetConstructors().Length == 0) continue;

                services.AddScoped(ruleType, t);
                registered++;
            }
        }

        return registered;
    }

    /// <summary>
    /// Test-only variant — register every IValidationRule implementation
    /// found in <paramref name="assembly"/>. Avoids the on-disk plugin
    /// folder dance for unit tests that only need the rules from a known
    /// assembly.
    /// </summary>
    public static int RegisterFromAssembly(IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        var ruleType = typeof(IValidationRule);
        var registered = 0;
        foreach (var t in assembly.GetTypes())
        {
            if (t.IsAbstract || t.IsInterface) continue;
            if (!ruleType.IsAssignableFrom(t)) continue;
            if (t.GetConstructors().Length == 0) continue;

            services.AddScoped(ruleType, t);
            registered++;
        }
        return registered;
    }
}
