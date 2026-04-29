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

    /// <summary>
    /// Phase F5 — maximum number of render attempts per
    /// <c>ScanRenderArtifact</c> before <see cref="PreRenderWorker"/>
    /// gives up and stamps <c>PermanentlyFailedAt</c>. Five is the v1
    /// rule of thumb: enough for transient adapter / decoder hiccups;
    /// short enough that a poison message stops spamming the logs.
    /// </summary>
    public int MaxRenderAttempts { get; set; } = 5;

    /// <summary>
    /// Phase F5 — minimum age (in days) a source blob must reach before
    /// the <c>SourceJanitorWorker</c> may evict it. Only blobs whose
    /// only-referencing case is in <c>Closed</c> or <c>Cancelled</c>
    /// state are eligible; this is an additional grace period on top of
    /// that. 30 days matches the v1 retention default.
    /// </summary>
    public int SourceRetentionDays { get; set; } = 30;

    /// <summary>
    /// Phase F5 — how often the <c>SourceJanitorWorker</c> wakes up and
    /// scans for evictable source blobs. One hour is the default;
    /// shorter values make sense in environments with tight disk
    /// budgets but trade off Postgres pressure.
    /// </summary>
    public int SourceJanitorIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// FU-3 (Sprint 6) — defence-in-depth hook for the astronomically
    /// unlikely cross-tenant SHA-256 collision case. <c>DiskImageStore</c>
    /// is content-addressed: <c>source/{hash[0..2]}/{hash}.{ext}</c>.
    /// If two tenants ever produced byte-identical scans, both
    /// <see cref="Core.Entities.ScanArtifact"/> rows would point at the
    /// same blob and <see cref="SourceJanitorWorker"/>'s per-tenant
    /// "still-referenced" check could race-evict the blob from under
    /// the other tenant.
    ///
    /// <para>
    /// When <c>true</c>, <see cref="SourceJanitorWorker"/> would refuse
    /// to evict a content-addressed blob whose hash appears in any other
    /// tenant's <c>scan_artifacts</c> rows. <strong>Currently NOT
    /// enforced</strong> — declared as a future-hardening hook (FU-3).
    /// Enforcement requires Sprint-5's
    /// <c>ITenantContext.SetSystemContext()</c> mechanism plus an
    /// <c>inspection.scan_artifacts</c> RLS opt-in clause; both are out
    /// of scope for FU-3. See <c>docs/ARCHITECTURE.md</c> §7.7.1
    /// (Cross-tenant blob collision posture) for the full rationale and
    /// the deferred-enforcement plan. Default: <c>false</c>.
    /// </para>
    /// </summary>
    public bool EnforceCrossTenantBlobGuard { get; set; } = false;
}
