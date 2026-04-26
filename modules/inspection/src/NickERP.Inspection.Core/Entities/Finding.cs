using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// One observation an analyst recorded during a review. A review can
/// have many findings ("this region looks like organic material",
/// "manifest says steel but image shows hollow shell", etc.). Severity
/// drives whether a finding feeds the verdict's decision logic.
/// </summary>
public sealed class Finding : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AnalystReviewId { get; set; }
    public AnalystReview? Review { get; set; }

    /// <summary>Free-form short code — "anomaly.organic", "manifest.mismatch", "shielding". Module convention rather than enforced enum.</summary>
    public string FindingType { get; set; } = string.Empty;

    /// <summary>Severity bucket — "info", "warning", "critical". Drives verdict surfacing.</summary>
    public string Severity { get; set; } = "info";

    /// <summary>JSON ROI box: <c>{x,y,w,h,artifactId}</c>. Where in which artifact the finding was made.</summary>
    public string LocationInImageJson { get; set; } = "{}";

    /// <summary>Free-text analyst note.</summary>
    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long TenantId { get; set; }
}
