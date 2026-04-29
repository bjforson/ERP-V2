using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Inference.Abstractions;
using NickERP.Inspection.Inference.OnnxRuntime;

namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// Direct DI wiring for <see cref="IContainerNumberRecognizer"/>. Plugin-loader
/// discovery via <c>NickERP.Platform.Plugins</c> remains the production path;
/// this is the convenience entry point for hosts that bypass the loader and
/// for the smoke-test rig.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Stable DI key matching the plugin's typeCode.</summary>
    public const string ServiceKey = "container-ocr-florence2";

    /// <summary>
    /// Register <see cref="ContainerNumberRecognizer"/> as a keyed singleton.
    /// The caller supplies an <see cref="ModelArtifact"/> provider (the host's
    /// model registry resolves <c>(ModelId, ModelVersion)</c> to a disk
    /// artifact) and optionally tweaks <see cref="ContainerOcrConfig"/>. The
    /// underlying <see cref="IInferenceRunner"/> is resolved from DI — wire
    /// <c>AddInferenceOnnxRuntime()</c> first.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="artifactProvider">Callable that returns the resolved <see cref="ModelArtifact"/> on first recognise.</param>
    /// <param name="configureOptions">Optional config tweak.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddInferenceContainerOcr(
        this IServiceCollection services,
        Func<IServiceProvider, ModelArtifact> artifactProvider,
        Action<ContainerOcrConfig>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(artifactProvider);

        var config = new ContainerOcrConfig();
        configureOptions?.Invoke(config);
        services.AddSingleton(config);

        services.AddSingleton<ContainerNumberRecognizer>(sp =>
        {
            // Prefer the keyed onnx-runtime runner; fall back to the unkeyed
            // default registration if the host wired it up that way.
            var runner =
                sp.GetKeyedService<IInferenceRunner>(NickERP.Inspection.Inference.OnnxRuntime.ServiceCollectionExtensions.ServiceKey)
                ?? sp.GetService<IInferenceRunner>()
                ?? throw new InvalidOperationException(
                    "AddInferenceContainerOcr requires an IInferenceRunner in DI. " +
                    "Call services.AddInferenceOnnxRuntime() first.");
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return new ContainerNumberRecognizer(
                runner,
                () => artifactProvider(sp),
                config,
                loggerFactory.CreateLogger<ContainerNumberRecognizer>());
        });

        services.AddKeyedSingleton<IContainerNumberRecognizer>(
            ServiceKey,
            (sp, _) => sp.GetRequiredService<ContainerNumberRecognizer>());
        services.AddSingleton<IContainerNumberRecognizer>(sp => sp.GetRequiredService<ContainerNumberRecognizer>());
        return services;
    }
}
