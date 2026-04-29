namespace NickERP.Platform.Audit.Database.Entities;

/// <summary>
/// Sprint 11 / P2 — pre-configured pairing of an edge node id and a
/// tenant the edge is allowed to replay events for. Composite PK
/// (<see cref="EdgeNodeId"/>, <see cref="TenantId"/>).
///
/// <para>
/// Lives in the <c>audit</c> schema because it gates writes to
/// <c>audit.events</c>; conceptually part of the same subsystem and
/// shares the DbContext + migration cadence. <b>Not under tenant RLS</b>
/// — this is suite-wide reference data: the server reads it under
/// system context to authorize an incoming edge replay batch. A
/// per-tenant policy on this table would create a chicken-and-egg
/// situation (the lookup itself would need to be tenant-scoped, but
/// the lookup is what determines tenancy).
/// </para>
///
/// <para>
/// Operator-managed: rows are inserted by an admin endpoint (out of
/// scope for v0; v0 expects rows to be seeded directly via SQL), one
/// row per (edge, tenant) pair the edge is allowed to ship events
/// for. Removing a row immediately deauthorizes future replays for
/// that pairing — already-replayed audit rows stay; in-flight batches
/// reject with a per-entry error.
/// </para>
/// </summary>
public sealed class EdgeNodeAuthorization
{
    /// <summary>Stable id of the edge node, e.g. <c>edge-tema-1</c>. Composite PK component.</summary>
    public string EdgeNodeId { get; set; } = string.Empty;

    /// <summary>Tenant the edge is authorized to replay events for. Composite PK component.</summary>
    public long TenantId { get; set; }

    /// <summary>When the row was authorized.</summary>
    public DateTimeOffset AuthorizedAt { get; set; }

    /// <summary>Operator who authorized the pairing. Nullable for seeded rows.</summary>
    public Guid? AuthorizedByUserId { get; set; }
}
