using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// One image / channel / side-view produced by a <see cref="Scan"/>.
/// Vendor-neutral shape; concrete adapters parse vendor formats and emit
/// these. Artifacts are content-addressable via <see cref="ContentHash"/>;
/// the pre-rendering pipeline keys thumbnails + previews against this hash.
/// </summary>
public sealed class ScanArtifact : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ScanId { get; set; }
    public Scan? Scan { get; set; }

    /// <summary>What kind of artifact: <c>Primary</c>, <c>SideView</c>, <c>Material</c>, <c>IR</c>, <c>ROI</c>, etc. Adapter-defined; documented per scanner.</summary>
    public string ArtifactKind { get; set; } = "Primary";

    /// <summary>Storage URI — disk path, blob key, etc. Resolved by the image pipeline when serving.</summary>
    public string StorageUri { get; set; } = string.Empty;

    /// <summary>MIME type of the artifact (image/jpeg, image/png, image/tiff, etc.).</summary>
    public string MimeType { get; set; } = "image/png";

    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public int Channels { get; set; } = 1;

    /// <summary>SHA-256 hex digest of the raw bytes. Stable; used as cache + ETag key.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Adapter-specific metadata (capture parameters, device serial, etc.) as JSON.</summary>
    public string MetadataJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public long TenantId { get; set; }
}
