using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Inference.Abstractions;

namespace NickERP.Inspection.Inference.OnnxRuntime;

/// <summary>
/// Direct DI wiring for the ONNX Runtime <see cref="IInferenceRunner"/>.
/// Plugin-style discovery via <c>NickERP.Platform.Plugins</c> remains the
/// preferred path for production hosts; this is a convenience for tests
/// and for hosts that want to bypass the plugin loader.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Stable DI key matching <see cref="OnnxRuntimeRunner.TypeCode"/>; resolve via <c>GetRequiredKeyedService&lt;IInferenceRunner&gt;("onnx-runtime")</c>.</summary>
    public const string ServiceKey = "onnx-runtime";

    /// <summary>
    /// Register <see cref="OnnxRuntimeRunner"/> as a keyed singleton under
    /// <see cref="ServiceKey"/>. Also registered as the default
    /// <see cref="IInferenceRunner"/> when nothing else is wired up so
    /// tests that resolve the unkeyed service still work.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="configureOptions">
    /// Optional callback to populate an <see cref="InferenceRunnerConfig"/> whose
    /// <c>IntraOpThreads</c> / <c>InterOpThreads</c> are applied to every loaded session.
    /// When <c>null</c>, ORT picks its own auto-detected thread defaults.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddInferenceOnnxRuntime(
        this IServiceCollection services,
        Action<InferenceRunnerConfig>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OnnxRuntimeRunner>(sp =>
        {
            InferenceRunnerConfig? cfg = null;
            if (configureOptions is not null)
            {
                cfg = new InferenceRunnerConfig { PreferredExecutionProvider = "CPU" };
                configureOptions(cfg);
            }
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return new OnnxRuntimeRunner(loggerFactory, cfg);
        });
        services.AddKeyedSingleton<IInferenceRunner>(
            ServiceKey,
            (sp, _) => sp.GetRequiredService<OnnxRuntimeRunner>());
        services.TryAddSingletonInferenceRunner<OnnxRuntimeRunner>();
        return services;
    }

    /// <summary>Register <typeparamref name="T"/> as the default unkeyed <see cref="IInferenceRunner"/> if none is set.</summary>
    private static IServiceCollection TryAddSingletonInferenceRunner<T>(this IServiceCollection services)
        where T : class, IInferenceRunner
    {
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].ServiceType == typeof(IInferenceRunner) && services[i].ServiceKey is null)
            {
                return services;
            }
        }
        services.AddSingleton<IInferenceRunner>(sp => sp.GetRequiredService<T>());
        return services;
    }
}
