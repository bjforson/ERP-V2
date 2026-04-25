namespace NickERP.Platform.Identity.Entities;

/// <summary>
/// A non-human caller — a Cloudflare Access service token, a scheduled
/// job, a scanner-pipeline worker. Has its own scope set and its own
/// audit identity; never collapses into <see cref="IdentityUser"/>.
/// </summary>
/// <remarks>
/// <para>
/// Cloudflare Access service tokens carry a <c>cf-access-client-id</c>
/// claim that the platform middleware maps to <see cref="TokenClientId"/>.
/// Rotation happens at the CF dashboard; the platform stores only the
/// public id (never the secret).
/// </para>
/// <para>
/// Service-token actions in audit logs surface as the
/// <see cref="DisplayName"/> rather than an email address, so
/// auditors can distinguish "the FS6000 worker did X" from "Angela
/// did X".
/// </para>
/// </remarks>
public class ServiceTokenIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The CF Access service-token client id (public, opaque, ~36 chars).</summary>
    public string TokenClientId { get; set; } = string.Empty;

    /// <summary>Friendly label surfaced in admin UI + audit log entries.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// What this token is for, free text — "FS6000 scanner ingestion",
    /// "ICUMS submission worker", "nightly Tally journal sync", etc.
    /// </summary>
    public string? Purpose { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Wall-clock at which the CF service token expires. Nullable
    /// because some tokens are non-expiring.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public long TenantId { get; set; } = 1;

    public List<ServiceTokenScope> Scopes { get; set; } = new();
}

/// <summary>
/// Link table — a service token's scope grants. Identical lifecycle
/// semantics to <see cref="UserScope"/>: time-bounded, revocable,
/// retained for audit.
/// </summary>
public class ServiceTokenScope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServiceTokenIdentityId { get; set; }
    public string AppScopeCode { get; set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }

    public long TenantId { get; set; } = 1;

    public ServiceTokenIdentity ServiceToken { get; set; } = null!;
}
