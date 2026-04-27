using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddHostedService<PreRenderWorker>();
        // Phase F5 — periodic eviction of source blobs once the
        // referencing case is closed/cancelled and older than
        // ImagingOptions.SourceRetentionDays.
        services.AddHostedService<SourceJanitorWorker>();

        return services;
    }
}
