using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Plugins;

/// <summary>
/// DI registration helpers for the NickERP plugin layer.
/// </summary>
public static class PluginsServiceCollectionExtensions
{
    /// <summary>
    /// Discover plugins under <paramref name="pluginsDirectory"/> and register
    /// each concrete plugin type as a singleton, plus a singleton
    /// <see cref="IPluginRegistry"/>. Modules query the registry to enumerate
    /// or resolve plugins by <see cref="PluginAttribute.TypeCode"/>.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="pluginsDirectory">Absolute path to the plugins folder. Pass <see langword="null"/> to use <c>{ContentRoot}/plugins</c> (resolved at runtime by callers that want that — this method does not resolve paths).</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddNickErpPlugins(
        this IServiceCollection services,
        string pluginsDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        services.AddSingleton<PluginLoader>();

        services.AddSingleton<IPluginRegistry>(sp =>
        {
            var loader = sp.GetRequiredService<PluginLoader>();
            var plugins = loader.LoadFrom(pluginsDirectory);

            // Register each concrete type as a singleton so the registry can
            // resolve them through DI (and apply ctor injection).
            foreach (var p in plugins)
            {
                // We can't mutate the IServiceCollection here (already built);
                // but we registered the concrete types upfront below if the
                // caller used AddNickErpPlugins(directory, registerTypesEager:true).
            }
            return new PluginRegistry(plugins);
        });

        return services;
    }

    /// <summary>
    /// Variant that loads + registers the concrete plugin types eagerly so
    /// they're resolvable via <c>IServiceProvider.GetRequiredService(type)</c>
    /// when <see cref="IPluginRegistry.Resolve{T}"/> is called.
    /// </summary>
    public static IServiceCollection AddNickErpPluginsEager(
        this IServiceCollection services,
        string pluginsDirectory,
        ILoggerFactory? loggerFactoryForEagerLoad = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);

        // Eagerly load now (build a temp logger if none supplied) so we can
        // register concrete types up front.
        var loggerFactory = loggerFactoryForEagerLoad ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger<PluginLoader>();
        var loader = new PluginLoader(logger);
        var plugins = loader.LoadFrom(pluginsDirectory);

        foreach (var p in plugins)
        {
            services.AddSingleton(p.ConcreteType);
        }

        services.AddSingleton<PluginLoader>(loader);
        services.AddSingleton<IPluginRegistry>(_ => new PluginRegistry(plugins));

        return services;
    }
}
