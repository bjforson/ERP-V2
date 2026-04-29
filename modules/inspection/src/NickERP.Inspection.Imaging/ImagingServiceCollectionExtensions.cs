using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Telemetry;

namespace NickERP.Inspection.Imaging;

/// <summary>
/// One-line wiring for the image pipeline. Hosts call
/// <c>builder.Services.AddNickErpImaging(builder.Configuration)</c> in
/// <c>Program.cs</c>. Registers <see cref="IImageRenderer"/>, the disk
/// <see cref="IImageStore"/>, and the <see cref="PreRenderWorker"/>
/// background service. Reads <see cref="ImagingOptions"/> from the
/// <c>NickErp:Inspection:Imaging</c> config section.
/// </summary>
public static class ImagingServiceCollectionExtensions
{
    public static IServiceCollection AddNickErpImaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ImagingOptions>()
            .Bind(configuration.GetSection(ImagingOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IImageRenderer, ImageSharpImageRenderer>();
        services.AddSingleton<IImageStore, DiskImageStore>();

        // Sprint 9 / FU-host-status — register each worker as a singleton,
        // then resolve it for both the hosted-service slot AND the
        // IBackgroundServiceProbe slot. Critical invariant: ONE worker
        // instance per worker class. If we used AddHostedService<T>()
        // alone the host creates a separate instance, and the probe
        // registration resolves a different one, so /healthz/workers
        // would always report "never ticked".
        services.AddSingleton<PreRenderWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<PreRenderWorker>());
        services.AddSingleton<IBackgroundServiceProbe>(sp => sp.GetRequiredService<PreRenderWorker>());

        // Phase F5 — periodic eviction of source blobs once the
        // referencing case is closed/cancelled and older than
        // ImagingOptions.SourceRetentionDays.
        services.AddSingleton<SourceJanitorWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<SourceJanitorWorker>());
        services.AddSingleton<IBackgroundServiceProbe>(sp => sp.GetRequiredService<SourceJanitorWorker>());

        return services;
    }
}
