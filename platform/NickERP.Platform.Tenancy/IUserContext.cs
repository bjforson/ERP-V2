namespace NickERP.Platform.Tenancy;

/// <summary>
/// Per-request handle to the current user's id. Sprint 9 / FU-userid —
/// mirrors <see cref="ITenantContext"/>, but for the user dimension. The
/// <see cref="TenantConnectionInterceptor"/> reads this on every connection
/// open and pushes it to Postgres as <c>SET app.user_id</c>; user-scoped
/// RLS policies (e.g. <c>tenant_user_isolation_notifications</c> on
/// <c>audit.notifications</c>) then filter rows on the session-local value.
/// </summary>
/// <remarks>
/// <para>
/// Lives in the tenancy assembly rather than identity for the same reason
/// <see cref="ITenantContext"/> does: the connection interceptor that
/// consumes both contexts must not have an upward dependency on identity
/// (modules that don't load identity still want RLS plumbing). The
/// resolution middleware reads the canonical <c>nickerp:id</c> claim by
/// string name without referencing the identity layer.
/// </para>
/// <para>
/// When unresolved (anonymous request, background job that hasn't
/// impersonated yet), <see cref="UserId"/> is <see cref="Guid.Empty"/> and
/// <see cref="IsResolved"/> is <c>false</c>; the interceptor pushes the
/// fail-closed default <c>'00000000-0000-0000-0000-000000000000'::uuid</c>
/// in that case so user-scoped RLS policies match nothing.
/// </para>
/// </remarks>
public interface IUserContext
{
    /// <summary>
    /// Current user id. <see cref="Guid.Empty"/> when <see cref="IsResolved"/>
    /// is false (no active user — anonymous or background job).
    /// </summary>
    Guid UserId { get; }

    /// <summary>
    /// <c>true</c> after <see cref="SetUser"/> has run; <c>false</c> before
    /// resolution or for unauthenticated / background contexts.
    /// </summary>
    bool IsResolved { get; }

    /// <summary>
    /// Replace the current user. Called by the resolution middleware (or
    /// by background jobs that explicitly impersonate, though FU-userid
    /// does not introduce any).
    /// </summary>
    void SetUser(Guid userId);
}

/// <summary>
/// Default <see cref="IUserContext"/> — a per-request scoped service.
/// </summary>
public sealed class UserContext : IUserContext
{
    public Guid UserId { get; private set; }
    public bool IsResolved { get; private set; }

    public void SetUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId must not be Guid.Empty.", nameof(userId));
        }
        UserId = userId;
        IsResolved = true;
    }
}
