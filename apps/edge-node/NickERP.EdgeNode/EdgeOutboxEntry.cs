namespace NickERP.EdgeNode;

/// <summary>
/// Sprint 11 / P2 — one row in the edge node's local SQLite write-buffer
/// (<c>edge_outbox</c>). Each row is a single audit-shaped event captured
/// while the edge is operating; the <see cref="EdgeReplayWorker"/> drains
/// the table FIFO when the central server is reachable.
///
/// <para>
/// v0 carries only audit events — every captured row's payload is a
/// JSON-serialised <c>DomainEvent</c>-shaped object that the server's
/// <c>POST /api/edge/replay</c> endpoint writes verbatim into
/// <c>audit.events</c>. Future event types (scan-captured, voucher-
/// disbursed) are a follow-up sprint; the mechanism stays the same,
/// only the dispatcher on the server-side changes.
/// </para>
///
/// <para>
/// Append-only. A successful replay stamps <see cref="ReplayedAt"/>;
/// nothing else mutates a row. Deletion is a separate operator-driven
/// pruning step that's out of scope for v0 (edge boxes have plenty of
/// disk, and replayed-but-kept rows are the audit trail of what the
/// edge has shipped).
/// </para>
/// </summary>
public sealed class EdgeOutboxEntry
{
    /// <summary>
    /// Surrogate key. SQLite assigns via <c>INTEGER PRIMARY KEY AUTOINCREMENT</c>.
    /// FIFO replay order is by ascending <see cref="Id"/>; the worker selects
    /// <c>WHERE ReplayedAt IS NULL ORDER BY Id ASC</c> on each tick.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Serialized event payload — JSON. v0 is the
    /// <c>DomainEvent</c>-shaped object the server writes verbatim
    /// into <c>audit.events</c>. Server-side parsing is governed by
    /// <see cref="EventTypeHint"/>.
    /// </summary>
    public string EventPayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Discriminator for which server-side replay handler should
    /// process this row. v0 supports a single value:
    /// <c>"audit.event.replay"</c>. Forward-compatible — additional
    /// hints land as the server adds dispatchers.
    /// </summary>
    public string EventTypeHint { get; set; } = string.Empty;

    /// <summary>
    /// When the edge captured this event. Preserved unchanged through
    /// the replay so the server records it as <c>OccurredAt</c> in
    /// <c>audit.events</c>; replay-time is recorded separately on the
    /// audit metadata.
    /// </summary>
    public DateTimeOffset EdgeTimestamp { get; set; }

    /// <summary>
    /// Stable id of the edge node that produced this row. Configured
    /// per-host (<c>EdgeNode:Id</c>). The server uses this to authorize
    /// the edge against the captured tenant.
    /// </summary>
    public string EdgeNodeId { get; set; } = string.Empty;

    /// <summary>
    /// Tenant the captured event belongs to. Preserved through the
    /// replay so the server replay handler knows the tenancy. Edge
    /// authorization (server-side) gates whether this edge node may
    /// replay events for this tenant — see
    /// <c>edge_node_authorizations</c>.
    /// </summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Null until the row has been successfully replayed. Set by the
    /// worker on the success ACK from the server.
    /// </summary>
    public DateTimeOffset? ReplayedAt { get; set; }

    /// <summary>
    /// Number of replay attempts the worker has made. Increments on
    /// every send (success or failure); only stays at zero for rows
    /// that were enqueued but never tried.
    /// </summary>
    public int ReplayAttempts { get; set; }

    /// <summary>
    /// Last server-side error message for this row. Populated by the
    /// worker when the server returns a 4xx (permanent rejection).
    /// 5xx responses are treated as transient and don't tag a permanent
    /// error — the attempt count still increments. Cleared if a later
    /// retry succeeds (the worker sets <see cref="ReplayedAt"/> and
    /// leaves this field, so an on-disk audit shows the recovered row's
    /// last error before the eventual success — that's intentional).
    /// </summary>
    public string? LastReplayError { get; set; }
}
