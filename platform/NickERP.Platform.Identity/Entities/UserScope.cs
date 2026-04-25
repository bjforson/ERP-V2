namespace NickERP.Platform.Identity.Entities;

/// <summary>
/// A grant of one <see cref="AppScope"/> to one <see cref="IdentityUser"/>.
/// The link table that the resolver assembles a user's effective scope
/// set from. Carries the audit fields needed to answer "who gave Angela
/// Finance.PettyCash.Approver and when".
/// </summary>
/// <remarks>
/// <para>
/// Time-bounded grants are supported via <see cref="ExpiresAt"/> — leave
/// it null for permanent grants. The resolver filters expired rows out;
/// it does not delete them so the audit trail stays intact.
/// </para>
/// <para>
/// Revocation is a write of <see cref="RevokedAt"/> + <see cref="RevokedByUserId"/>,
/// not a delete. We never lose the historical fact that Angela held
/// the scope between time T1 and time T2.
/// </para>
/// </remarks>
public class UserScope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IdentityUserId { get; set; }

    /// <summary>FK to <see cref="AppScope.Code"/> rather than its Guid id —
    /// makes raw inspection of the table readable without a join.</summary>
    public string AppScopeCode { get; set; } = string.Empty;

    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>The user who granted this scope. Should itself have an
    /// <c>Identity.GrantScope</c> scope at grant time.</summary>
    public Guid GrantedByUserId { get; set; }

    /// <summary>Optional expiry. Resolver excludes rows where now > ExpiresAt.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when the grant was revoked. Resolver excludes any row
    /// with a non-null value here, regardless of <see cref="ExpiresAt"/>.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }

    public string? Notes { get; set; }
    public long TenantId { get; set; } = 1;

    public IdentityUser User { get; set; } = null!;
}
