using NickERP.Inspection.Core.Retention;
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

    /// <summary>
    /// Sprint 39 — retention posture for this artifact. Mirrors
    /// <see cref="InspectionCase.RetentionClass"/>: cascades on case-level
    /// reclassify by default, but artifacts can be held independently of
    /// their parent case for evidentiary subpoena scope.
    /// </summary>
    public RetentionClass RetentionClass { get; set; } = RetentionClass.Standard;

    /// <summary>Sprint 39 — wallclock the current retention class was assigned.</summary>
    public DateTimeOffset? RetentionClassSetAt { get; set; }

    /// <summary>Sprint 39 — Identity user id of the operator who set the current retention class.</summary>
    public Guid? RetentionClassSetByUserId { get; set; }

    /// <summary>
    /// Sprint 39 — legal-hold flag. Set on every artifact under a case
    /// when <c>RetentionService.ApplyLegalHoldAsync</c> cascades; can
    /// also be set independently on a single artifact for narrow
    /// evidentiary subpoena scope.
    /// </summary>
    public bool LegalHold { get; set; }

    /// <summary>Sprint 39 — wallclock the legal hold was applied. Persists after release.</summary>
    public DateTimeOffset? LegalHoldAppliedAt { get; set; }

    /// <summary>Sprint 39 — operator who applied the most-recent hold. Persists after release.</summary>
    public Guid? LegalHoldAppliedByUserId { get; set; }

    /// <summary>Sprint 39 — free-text reason. Bounded to 500 chars. Persists after release.</summary>
    public string? LegalHoldReason { get; set; }

    public long TenantId { get; set; }
}
