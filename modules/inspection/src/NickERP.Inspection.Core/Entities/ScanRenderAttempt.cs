using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Phase F5 — durable retry / poison-message tracking for the image
/// pre-render pipeline.
///
/// <para>
/// One row per <c>(ScanArtifactId, Kind)</c>. The
/// <c>PreRenderWorker</c> increments <see cref="AttemptCount"/> on every
/// failed render. Once it reaches
/// <c>ImagingOptions.MaxRenderAttempts</c>, the worker stamps
/// <see cref="PermanentlyFailedAt"/>; the next sweep filters those rows
/// out so the worker stops retrying poison messages.
/// </para>
///
/// <para>
/// Sibling table to <see cref="ScanRenderArtifact"/> rather than columns
/// on the existing row because <see cref="ScanRenderArtifact"/> is only
/// inserted on success — the failure path needs a place to record state
/// without violating the NOT-NULL columns on the success row.
/// </para>
/// </summary>
public sealed class ScanRenderAttempt : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ScanArtifactId { get; set; }

    /// <summary><c>thumbnail</c> or <c>preview</c>. Matches <c>ScanRenderArtifact.Kind</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Number of render attempts so far (including the first). Starts at 0; bumped by <c>PreRenderWorker</c>.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Last error message recorded by the worker. Truncated to fit the column.</summary>
    public string? LastError { get; set; }

    /// <summary>UTC timestamp of the most recent attempt (success or failure).</summary>
    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>UTC timestamp the worker gave up on this artifact. Non-null rows are skipped on subsequent sweeps.</summary>
    public DateTimeOffset? PermanentlyFailedAt { get; set; }

    public long TenantId { get; set; }
}
