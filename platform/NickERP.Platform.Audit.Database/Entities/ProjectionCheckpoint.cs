namespace NickERP.Platform.Audit.Database.Entities;

/// <summary>
/// Single-row-per-projector bookmark indicating the latest
/// <c>audit.events.IngestedAt</c> the projector has already fanned out.
///
/// <para>
/// Intentionally NOT tenant-isolated and NOT under RLS — it is a system-wide
/// bookmark with one row per projector name, no tenant payload, no
/// user-visible content. Skipping RLS here avoids both (a) registering an
/// extra entry in <c>docs/system-context-audit-register.md</c> and (b)
/// adding the <c>OR current_setting('app.tenant_id') = '-1'</c> opt-in
/// clause for what is effectively a config table.
/// </para>
///
/// <para>
/// Granted SELECT/INSERT/UPDATE on <c>nscim_app</c> via the migration; the
/// projector reads the current row, computes the new value, and upserts.
/// </para>
/// </summary>
public sealed class ProjectionCheckpoint
{
    /// <summary>Stable name of the projector, e.g. <c>"AuditNotificationProjector"</c>.</summary>
    public string ProjectionName { get; set; } = string.Empty;

    /// <summary>
    /// Most recent <c>audit.events.IngestedAt</c> value the projector has
    /// already processed. The projector then fetches events with
    /// <c>IngestedAt &gt; this</c> on the next tick. Using <c>IngestedAt</c>
    /// (server clock) instead of <c>OccurredAt</c> (caller clock) avoids
    /// re-processing on clock skew.
    /// </summary>
    public DateTimeOffset LastIngestedAt { get; set; }

    /// <summary>Last write timestamp. For ops visibility (is the projector live?).</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
