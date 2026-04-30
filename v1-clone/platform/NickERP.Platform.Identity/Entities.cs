namespace NickERP.Platform.Identity;

/// <summary>
/// A canonical, in-system user. Created on first successful Cloudflare
/// Access login and looked up by <see cref="CfAccessSub"/> on every
/// subsequent request. The pre-identity-layer design hashed the email to
/// derive a stable user id; that worked for posting events but meant
/// the audit columns couldn't be foreign-keyed to a real row, role
/// assignments had nowhere to attach, and revoking a single operator
/// required either disabling them in CF Access (which severs every other
/// app) or rotating every email-derived hash. This entity is the
/// permanent home for an operator inside the NICKSCAN ERP platform.
/// </summary>
public sealed class User
{
    public Guid InternalUserId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The Cloudflare Access <c>sub</c> claim — stable per CF identity.
    /// Nullable for system / service accounts and for the dev fallback row.
    /// Unique when populated.
    /// </summary>
    public string? CfAccessSub { get; set; }

    /// <summary>Lower-cased email, unique across the tenant.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }

    public long TenantId { get; set; } = 1;
}

public enum UserStatus
{
    Active = 0,
    Disabled = 1,
    Pending = 2,
}

/// <summary>
/// E.164 phone number registered against a user — the WhatsApp resolver
/// uses this for inbound webhook lookup ("APPROVE PC-..." reply mapping)
/// and for outbound template messages.
/// </summary>
public sealed class UserPhone
{
    public Guid UserPhoneId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>E.164 string (e.g. "+233244123456"). Unique.</summary>
    public string PhoneE164 { get; set; } = string.Empty;

    public bool Verified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Static lookup table seeded on first migration. Smallint primary key
/// so role assignments are cheap to filter and join.
/// </summary>
public sealed class Role
{
    public short RoleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>
/// One row per (user, role[, site]) grant. <c>SiteId</c> nullable for
/// tenant-wide roles; populated for site-scoped grants like "Approver
/// for the Tema yard only". <c>ExpiresAt</c> nullable for permanent grants.
/// </summary>
/// <remarks>
/// Phase 1 of the role overhaul (2026-04-29) added <see cref="AuditFirm"/>
/// — a free-text firm name carried on <c>ExternalAuditor</c> grants for
/// audit-trail purposes. The column is nullable for back-compat with grants
/// made before the field existed; the role-grant flow only writes it for
/// <c>ExternalAuditor</c> rows and ignores it everywhere else.
/// </remarks>
public sealed class UserRole
{
    public Guid UserRoleId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public short RoleId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid GrantedByUserId { get; set; }
    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Free-text audit-firm name. Populated only for grants where the
    /// role is <c>ExternalAuditor</c>; ignored for every other role.
    /// </summary>
    public string? AuditFirm { get; set; }
}

/// <summary>
/// Granular permission catalogue. Seeded once at bootstrap from the
/// <c>NickFinance.WebApp.Identity.Permissions</c> string-constants
/// class — 52 rows across 11 categories
/// (<c>petty.voucher.approve</c>, <c>ar.invoice.issue</c>, etc.).
/// </summary>
/// <remarks>
/// <para>
/// Permission rows are referenced by name from <c>[Authorize(Policy =
/// Permissions.X)]</c> on the Razor pages, not by id. The smallint key
/// is purely a join optimisation for the <c>identity.role_permissions</c>
/// composite PK.
/// </para>
/// <para>
/// 2026-04-30 — added as part of the concentric grade rebuild that
/// replaced the 15 functional roles + role-list-policies model with
/// the NSCIM-style claim-based hierarchy.
/// </para>
/// </remarks>
public sealed class Permission
{
    public short PermissionId { get; set; }

    /// <summary>
    /// Stable string name (e.g. <c>"petty.voucher.approve"</c>) — the
    /// shape is <c>{module}.{noun}.{verb}</c>. Unique tenant-wide.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// One-line human-readable description shown by the HR grant UI's
    /// permission preview block.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// High-level grouping for UI display
    /// (e.g. <c>"petty"</c>, <c>"ar"</c>, <c>"banking"</c>). Free-form;
    /// the bootstrap CLI seeds this from the
    /// <c>Permissions.Descriptions</c> dictionary keys' first segment.
    /// </summary>
    public string? Category { get; set; }
}

/// <summary>
/// One row per (role, permission) grant. Composite PK; no surrogate id.
/// Seeded by the bootstrap CLI from
/// <c>NickFinance.WebApp.Identity.GradePermissions.ForGrade(roleName)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The bundle for each grade is the union of every parent's bundle —
/// the per-grade <c>GetXPermissions()</c> method calls its parent's
/// then <c>AddRange</c>s its delta — so a Bookkeeper row carries 18
/// permissions and a SuperAdmin row carries 52.
/// </para>
/// <para>
/// Legacy role names (the 6-name 2026-03 set + the 15-name 2026-04-29
/// interim set) deliberately get NO rows here — an accidentally-still-
/// granted legacy role resolves to ZERO permissions, which is the
/// right fail-closed default during the migration window.
/// </para>
/// </remarks>
public sealed class RolePermission
{
    public short RoleId { get; set; }
    public short PermissionId { get; set; }

    /// <summary>When this (role, permission) link was seeded / regranted.</summary>
    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>
/// Append-only record of every privileged action. Insert-only; never
/// updated, never deleted. The <see cref="Result"/> column distinguishes
/// successful actions, denied (failed authorization) attempts, and runtime
/// failures so the auditor can find both "what happened" and "what was
/// blocked".
/// </summary>
public sealed class SecurityAuditEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();

    /// <summary>Nullable for unauthenticated events (e.g. denied login).</summary>
    public Guid? UserId { get; set; }

    public SecurityAuditAction Action { get; set; }

    /// <summary>Free-form, e.g. "Voucher", "Invoice", "JournalEvent", "User".</summary>
    public string? TargetType { get; set; }

    /// <summary>Identifier of the target entity, stringified.</summary>
    public string? TargetId { get; set; }

    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Optional jsonb payload for action-specific context.</summary>
    public string? DetailsJson { get; set; }

    public SecurityAuditResult Result { get; set; } = SecurityAuditResult.Allowed;
    public long TenantId { get; set; } = 1;
}

public enum SecurityAuditAction
{
    Login = 0,
    RoleGranted = 1,
    RoleRevoked = 2,
    InvoiceIssued = 10,
    InvoiceVoided = 11,
    ReceiptRecorded = 12,
    VoucherSubmitted = 20,
    VoucherApproved = 21,
    VoucherRejected = 22,
    VoucherDisbursed = 23,
    JournalPosted = 30,
    JournalReversed = 31,
    PeriodSoftClosed = 40,
    PeriodHardClosed = 41,
    FloatCreated = 50,
    FloatClosed = 51,
    UserCreated = 60,
    UserDisabled = 61,
    AuthorizationDenied = 90,
    RuntimeFailure = 91,
}

public enum SecurityAuditResult
{
    Allowed = 0,
    Denied = 1,
    Failed = 2,
}

/// <summary>
/// Canonical role names. Kept here as constants so call sites use the
/// exact string the seed migration writes.
/// </summary>
public static class RoleNames
{
    public const string Custodian = "Custodian";
    public const string Approver = "Approver";
    public const string SiteManager = "SiteManager";
    public const string FinanceLead = "FinanceLead";
    public const string Auditor = "Auditor";
    public const string Admin = "Admin";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Custodian, Approver, SiteManager, FinanceLead, Auditor, Admin
    };
}
