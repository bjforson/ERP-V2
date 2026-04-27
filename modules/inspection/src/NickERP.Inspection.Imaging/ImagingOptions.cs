namespace NickERP.Inspection.Imaging;

/// <summary>
/// Configuration for the image pipeline. Bound to the
/// <c>NickErp:Inspection:Imaging</c> section in <c>appsettings.json</c>
/// (or env vars / user secrets as usual).
/// </summary>
public sealed class ImagingOptions
{
    public const string SectionName = "NickErp:Inspection:Imaging";

    /// <summary>
    /// Filesystem root for the image pipeline. Two subdirectories live
    /// under it: <c>source/</c> (verbatim adapter output, content-addressed)
    /// and <c>render/</c> (derived thumbnails + previews, scanArtifactId-keyed).
    /// </summary>
    public string StorageRoot { get; set; } = string.Empty;

    /// <summary>How often the pre-render worker polls for unrendered artifacts.</summary>
    public int WorkerPollIntervalSeconds { get; set; } = 5;

    /// <summary>How many artifacts to render in one worker cycle.</summary>
    public int WorkerBatchSize { get; set; } = 16;

    /// <summary>
    /// HTTP <c>Cache-Control: max-age</c> seconds on the served image
    /// endpoint. The artifacts are content-addressed (ETag = source hash)
    /// so 1 day local + 7 days CDN is safe; analyst flow tolerates stale.
    /// </summary>
    public int HttpCacheMaxAgeSeconds { get; set; } = 86400;

    /// <summary>HTTP <c>Cache-Control: s-maxage</c> seconds (CDN/shared cache).</summary>
    public int HttpCacheSharedMaxAgeSeconds { get; set; } = 604800;
}
