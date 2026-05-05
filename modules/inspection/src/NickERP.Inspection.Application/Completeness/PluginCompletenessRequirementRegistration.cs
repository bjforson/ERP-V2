using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Inspection.Core.Completeness;

namespace NickERP.Inspection.Application.Completeness;

/// <summary>
/// Sprint 48 / Phase C — host helper for picking up plugin-supplied
/// <see cref="ICompletenessRequirement"/> implementations from the
/// plugins folder without taking a hard project reference on the plugin
/// assembly.
///
/// <para>
/// Mirrors <see cref="NickERP.Inspection.Application.Validation.PluginValidationRuleRegistration"/>
/// in shape — a single plugin DLL can ship multiple completeness
/// requirements (e.g. CustomsGh's CMR-port-state +
/// regime-specific-documents pair). Reflection scan; assemblies load
/// into the default load context. Idempotent.
/// </para>
///
/// <para>
/// Hosts call <c>RegisterPluginCompletenessRequirements(services,
/// pluginsDir)</c> alongside the validation-rule registration pass.
/// Tests use <see cref="RegisterFromAssembly"/>.
/// </para>
/// </summary>
public static class PluginCompletenessRequirementRegistration
{
    /// <summary>
    /// Reflect over every assembly in <paramref name="pluginsDirectory"/>
    /// and register the <see cref="ICompletenessRequirement"/> types they
    /// expose. Safe to call at host startup before
    /// <c>builder.Build()</c>.
    /// </summary>
    /// <returns>Count of requirement types registered.</returns>
    public static int RegisterPluginCompletenessRequirements(
        IServiceCollection services,
        string pluginsDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (string.IsNullOrWhiteSpace(pluginsDirectory)) return 0;
        if (!Directory.Exists(pluginsDirectory)) return 0;

        var requirementType = typeof(ICompletenessRequirement);
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
                continue;
            }

            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types?.Where(t => t is not null).ToArray()! ?? Array.Empty<Type>();
            }

            foreach (var t in types)
            {
                if (t is null) continue;
                if (t.IsAbstract || t.IsInterface) continue;
                if (!requirementType.IsAssignableFrom(t)) continue;
                if (t.GetConstructors().Length == 0) continue;

                services.AddScoped(requirementType, t);
                registered++;
            }
        }

        return registered;
    }

    /// <summary>
    /// Test-only variant — register every
    /// <see cref="ICompletenessRequirement"/> implementation found in
    /// <paramref name="assembly"/>. Avoids the on-disk plugin folder
    /// dance for unit tests.
    /// </summary>
    public static int RegisterFromAssembly(IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        var requirementType = typeof(ICompletenessRequirement);
        var registered = 0;
        foreach (var t in assembly.GetTypes())
        {
            if (t.IsAbstract || t.IsInterface) continue;
            if (!requirementType.IsAssignableFrom(t)) continue;
            if (t.GetConstructors().Length == 0) continue;

            services.AddScoped(requirementType, t);
            registered++;
        }
        return registered;
    }
}
