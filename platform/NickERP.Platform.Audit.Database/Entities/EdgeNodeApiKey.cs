namespace NickERP.Platform.Audit.Database.Entities;

/// <summary>
/// Sprint 13 / P2-FU-edge-auth — per-edge-node API key for the
/// <c>/api/edge/replay</c> endpoint. Replaces (in parallel during the
/// rollout window) the Sprint 11 single-shared-secret <c>X-Edge-Token</c>
/// flow with per-node, revocable, rotation-friendly keys.
///
/// <para>
/// <b>Storage posture.</b> The plaintext key is NEVER stored. On
/// issuance the server (a) generates a high-entropy random key, (b)
/// HMAC-SHA256 hashes it under a server-side secret
/// (<c>EdgeAuth:HashKey</c> in config, falling back to an
/// <c>IDataProtector</c>-derived envelope key on hosts that haven't
/// configured it) and stores the hash in <see cref="KeyHash"/>, and
/// (c) shows the plaintext to the operator EXACTLY ONCE for transport
/// to the edge. Compromise of the audit DB alone (e.g. an unscoped
/// <c>SELECT</c> via <c>postgres</c>) does not yield usable keys —
/// the attacker would also need the host's <c>EdgeAuth:HashKey</c>
/// value (or its data-protection key ring fallback).
/// </para>
///
/// <para>
/// <b>Key prefix.</b> <see cref="KeyPrefix"/> is the first 8 chars
/// of the plaintext key — non-secret, used in operator UIs to
/// disambiguate which row a key belongs to without showing the full
/// key. Logs and audit events reference the prefix; the full key
/// only ever lives in the edge's local config.
/// </para>
///
/// <para>
/// <b>Lifecycle.</b>
/// <list type="number">
///   <item><description><c>active</c> — <see cref="IssuedAt"/>
///   populated, <see cref="RevokedAt"/> NULL, <see cref="ExpiresAt"/>
///   NULL or in the future. Accepted by the auth handler.</description></item>
///   <item><description><c>revoked</c> — <see cref="RevokedAt"/>
///   populated. Rejected immediately on every replay.</description></item>
///   <item><description><c>expired</c> — <see cref="ExpiresAt"/>
///   in the past. Rejected immediately. Operators may re-issue
///   under the same edge node.</description></item>
/// </list>
/// Rows are NEVER deleted (audit posture); they remain in the table
/// for forensic / audit purposes after revocation.
/// </para>
///
/// <para>
/// <b>Tenancy.</b> Tenant-scoped via <see cref="TenantId"/>; protected
/// by <c>tenant_isolation_edge_node_api_keys</c> RLS policy mirroring
/// the FU-icums-signing pattern. The auth handler reads under
/// <see cref="NickERP.Platform.Tenancy.ITenantContext.SetSystemContext"/>
/// because the edge presents a key BEFORE any tenant context is
/// resolved (tenant resolution is a downstream step that uses the
/// authorized tenant set from <c>edge_node_authorizations</c>); the
/// per-tenant scoping is asserted at lookup time by matching the
/// incoming edge node's authorized tenant set against this row's
/// <c>TenantId</c>.
/// </para>
/// </summary>
public sealed class EdgeNodeApiKey
{
    /// <summary>Stable id for this key row. PK.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Tenant scope. The edge node may carry events for multiple tenants
    /// (per <c>edge_node_authorizations</c>) but the API key itself is
    /// issued and revoked under a single tenant — matching the operator
    /// who clicked "issue".
    /// </summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Logical edge node id, e.g. <c>edge-tema-1</c>. Matches the
    /// <c>EdgeNodeAuthorization.EdgeNodeId</c> string. Multiple keys
    /// per edge are allowed (rotation overlap window).
    /// </summary>
    public string EdgeNodeId { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 hash of the plaintext key under the server's hash
    /// key. <see cref="EdgeAuthHandler"/> hashes the presented key the
    /// same way and uses <c>FixedTimeEquals</c> against this column
    /// to authenticate. Indexed UNIQUE for direct lookup.
    /// </summary>
    public byte[] KeyHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// First 8 chars of the plaintext key. Non-secret — operator UIs
    /// display this so two issued keys for the same edge are
    /// distinguishable in the keys table.
    /// </summary>
    public string KeyPrefix { get; set; } = string.Empty;

    /// <summary>When the key was generated and stored.</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// Optional hard expiry. If set, the auth handler rejects the key
    /// after this point even if not explicitly revoked. NULL = no
    /// hard expiry.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// When the operator revoked the key. NULL = active. Once set
    /// the row is immutable.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Operator-supplied description (e.g. "tema lane 2 reissue
    /// 2026-04-30"). Optional but encouraged for ops trail.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// User who issued the key. Populated by the admin UI from the
    /// caller's <c>nickerp:id</c> claim. NULL for seeded rows or
    /// system-issued keys.
    /// </summary>
    public Guid? CreatedByUserId { get; set; }
}
