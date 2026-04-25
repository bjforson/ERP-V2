namespace NickERP.Platform.Identity.Entities;

/// <summary>
/// The canonical record of a person (or pseudo-person) in the NickERP suite.
/// One row per real human; their email is the natural key matched against
/// the <c>email</c> claim in the Cloudflare Access JWT (or any future IdP).
/// </summary>
/// <remarks>
/// <para>
/// Per-app profile records (HR's <c>Employees</c>, NSCIM's analyst record,
/// Finance's custodian record, etc.) carry their own keys and reference
/// this user by canonical <see cref="Id"/> via adapter shims (Track C.2).
/// </para>
/// <para>
/// Soft-delete only — set <see cref="IsActive"/> to <c>false</c>. Hard
/// delete is forbidden because <see cref="UserScope"/>, audit events, and
/// payroll/finance journals all carry foreign keys that must remain
/// resolvable for the legal retention period (Companies Act 992 §126:
/// 6 years; we apply 7).
/// </para>
/// </remarks>
public class IdentityUser
{
    /// <summary>Stable canonical identifier. Never changes for the life of the user.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Lowercased, trimmed email used to match the Cloudflare Access JWT
    /// <c>email</c> claim. Unique within a tenant.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Cached normalised form (UPPER) for case-insensitive matching at
    /// query time. Maintained automatically; do not write directly.
    /// </summary>
    public string NormalizedEmail { get; set; } = string.Empty;

    /// <summary>Free-text display name shown in UIs. Optional.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// <c>false</c> = deprovisioned. JWTs for inactive users are still
    /// validated by the middleware but <c>IIdentityResolver</c>
    /// rejects the resolution, blocking access at the app boundary.
    /// (<c>IIdentityResolver</c> ships in A.2.5.)
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Wall-clock time of the most recent successful resolution. Indexed
    /// for "show me users who have not signed in for X days" reporting.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Multi-tenant isolation key. Aligns with Platform.Tenancy contract.</summary>
    public long TenantId { get; set; } = 1;

    public List<UserScope> Scopes { get; set; } = new();
}
