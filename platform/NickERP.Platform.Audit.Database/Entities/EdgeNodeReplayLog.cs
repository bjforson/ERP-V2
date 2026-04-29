namespace NickERP.Platform.Audit.Database.Entities;

/// <summary>
/// Sprint 11 / P2 — one row per edge replay batch successfully
/// processed by the server. Records edge id, when the replay was
/// processed, batch size + outcome counts, and (if any) the
/// per-entry failures as a JSON blob.
///
/// <para>
/// Lives in the <c>audit</c> schema for the same reason as
/// <c>edge_node_authorizations</c> — the audit subsystem owns the
/// entire edge-replay surface. Not under tenant RLS: a single replay
/// batch can carry events for multiple tenants, so a per-tenant
/// policy can't filter the whole row. Reads are operator-facing only
/// (an admin "edge activity" page).
/// </para>
///
/// <para>
/// Append-only by convention. v0 doesn't expose a delete path; ops
/// can prune via a SQL admin tool when the table grows beyond the
/// retention window. No FK to <c>audit.events</c> — the per-event
/// audit trail is the events themselves; this table is the per-batch
/// summary for ops visibility.
/// </para>
/// </summary>
public sealed class EdgeNodeReplayLog
{
    /// <summary>Surrogate key. Defaults via Postgres <c>gen_random_uuid()</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>Edge node that submitted the batch.</summary>
    public string EdgeNodeId { get; set; } = string.Empty;

    /// <summary>When the server processed the batch.</summary>
    public DateTimeOffset ReplayedAt { get; set; }

    /// <summary>Number of events in the batch.</summary>
    public int EventCount { get; set; }

    /// <summary>Number of events the server accepted (audit row written).</summary>
    public int OkCount { get; set; }

    /// <summary>Number of events the server rejected (per-entry error).</summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// JSON array of per-failure metadata, e.g.
    /// <c>[{"index":2,"error":"tenant 99 not authorized"}]</c>.
    /// Null when <see cref="FailedCount"/> is zero.
    /// </summary>
    public string? FailuresJson { get; set; }
}
