using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// One pre-rendered derivative of a <see cref="ScanArtifact"/> — a
/// 256 px thumbnail or 1024 px preview produced by the image pipeline
/// (ARCHITECTURE §7.7). The source <see cref="ScanArtifact"/> is what
/// the scanner adapter handed back; this row is what the analyst viewer
/// and the cases-list thumbnails actually serve.
///
/// Keyed by (<see cref="ScanArtifactId"/>, <see cref="Kind"/>) — at most
/// one row per artifact per kind. Re-renders overwrite (same row, new
/// <see cref="ContentHash"/>) rather than appending.
/// </summary>
public sealed class ScanRenderArtifact : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ScanArtifactId { get; set; }
    public ScanArtifact? ScanArtifact { get; set; }

    /// <summary><c>thumbnail</c> or <c>preview</c>. See <c>NickERP.Inspection.Imaging.RenderKinds</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Storage URI — resolved by the image pipeline's <c>IImageStore</c>.</summary>
    public string StorageUri { get; set; } = string.Empty;

    public int WidthPx { get; set; }
    public int HeightPx { get; set; }

    /// <summary>MIME type of the rendered bytes (currently always <c>image/png</c>).</summary>
    public string MimeType { get; set; } = "image/png";

    /// <summary>SHA-256 hex digest of the rendered bytes; used as ETag.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>UTC timestamp the render was produced.</summary>
    public DateTimeOffset RenderedAt { get; set; }

    public long TenantId { get; set; }
}
