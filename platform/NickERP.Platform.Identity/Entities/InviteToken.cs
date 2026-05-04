namespace NickERP.Platform.Identity.Entities;

/// <summary>
/// Sprint 21 / Phase B — one-time invite token for the first-user
/// invite flow. Issued by a platform admin (typically alongside a
/// new tenant create), redeemed by the invitee through
/// <c>/invite/accept/{token}</c> on the portal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Posture.</b> The plaintext token is generated once, embedded
/// in the email body, and discarded server-side. The DB stores only
/// an HMAC-SHA256 hash plus a non-secret 8-char prefix for support
/// triage. Compromise of the platform DB alone does not yield usable
/// tokens — the attacker would need both the row and the host's
/// HMAC key (or the data-protection key ring fallback used by the
/// portal-side <c>InviteTokenHashEnvelope</c>).
/// </para>
/// <para>
/// <b>Single-use enforcement.</b> A unique partial index on
/// <see cref="TokenHash"/> filtered to
/// <c>RedeemedAt IS NULL AND RevokedAt IS NULL</c> keeps the
/// redemption race-safe via Postgres' unique-constraint atomicity:
/// concurrent redemptions can both pass the read-side validation,
/// but only one can flip RedeemedAt; the loser hits a unique
/// violation and the service returns "already redeemed".
/// </para>
/// <para>
/// <b>Tenancy.</b> Tenant-scoped via <see cref="TenantId"/> with
/// <c>ENABLE</c> + <c>FORCE</c> ROW LEVEL SECURITY. The redemption
/// path uses <c>NickERP.Platform.Tenancy.ITenantContext.SetSystemContext</c>
/// because the tenant is on the row itself; the policy admits this
/// via the standard <c>OR app.tenant_id = '-1'</c> opt-in clause
/// shared with <c>audit.edge_node_api_keys</c>. Registered in
/// <c>docs/system-context-audit-register.md</c>.
/// </para>
/// </remarks>
public sealed class InviteToken
{
    /// <summary>Stable id for this invite row. PK.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant the invite grants access to.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Lowercased, trimmed email of the invitee. Matches the
    /// <c>IdentityUser.NormalizedEmail</c> rule (RFC 5321 320-char
    /// max). Indexed for "do they already have a row" lookup at
    /// redemption time.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 hash of the plaintext token under the host's
    /// invite-token hash key. <c>InviteService</c> (in
    /// <c>NickERP.Platform.Identity.Database</c>) computes this on
    /// issue and on redeem; equality compared via
    /// <c>FixedTimeEquals</c> (the unique index does the lookup,
    /// FixedTimeEquals is defence-in-depth).
    /// </summary>
    public byte[] TokenHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// First 8 chars of the plaintext token. Non-secret — operator
    /// UIs surface this so two issued invites for the same email
    /// are distinguishable in audit / support contexts.
    /// </summary>
    public string TokenPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Comma-separated list of role codes the invitee will receive
    /// once they redeem (e.g. <c>Tenant.Admin</c>). Default for the
    /// first-user flow is <c>Tenant.Admin</c>; future flows may pass
    /// narrower roles.
    /// </summary>
    public string IntendedRoles { get; set; } = "Tenant.Admin";

    /// <summary>When the invite was issued.</summary>
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>
    /// Hard expiry. If <see cref="RedeemedAt"/> is null and the
    /// current time has passed this, redemption is rejected with
    /// "expired". Default 72 hours after <see cref="IssuedAt"/>;
    /// configurable via <c>Email:Invite:DefaultExpiryHours</c>.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Identity user id of the operator who issued the invite. May
    /// be null for the bootstrap path where the platform admin's
    /// identity isn't yet stamped on the request (e.g. seeded
    /// invite via ops scripts).
    /// </summary>
    public Guid? IssuedByUserId { get; set; }

    /// <summary>
    /// When the invitee redeemed the invite. Null while pending;
    /// flipped to <see cref="DateTimeOffset.UtcNow"/> on successful
    /// redeem. Once set the row is immutable.
    /// </summary>
    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>
    /// Identity user id created (or matched) at redemption time.
    /// Null until <see cref="RedeemedAt"/> is set.
    /// </summary>
    public Guid? RedeemedByUserId { get; set; }

    /// <summary>
    /// When the invite was revoked by an admin (without redemption).
    /// Mutually exclusive with <see cref="RedeemedAt"/> — once
    /// either is set the partial unique index drops the row from
    /// the active set.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Operator who revoked the invite (if applicable). Null while
    /// pending or if the invite was redeemed before being revoked.
    /// </summary>
    public Guid? RevokedByUserId { get; set; }
}
