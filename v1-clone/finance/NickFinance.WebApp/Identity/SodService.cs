using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Separation-of-Duties enforcement at role-grant time. After the
/// 2026-04-30 concentric-grade overhaul this service shrank dramatically:
/// the 5 hard-pair / 7 warn-pair forbidden-combination catalogue from the
/// flat-15-role model evaporated because composition makes most of those
/// pairs structurally impossible (one grade slot per user, with each
/// grade strictly extending its parent).
/// </summary>
/// <remarks>
/// <para>
/// What stays here is the sanity guard at grant time:
/// <list type="bullet">
///   <item><description><b>Audit-vs-ops exclusion.</b> Users on the ops ladder
///     (Viewer → SuperAdmin) cannot also hold an audit grade
///     (InternalAuditor / ExternalAuditor). Audit independence requires
///     the auditor never operate the system they audit.</description></item>
///   <item><description><b>ExternalAuditor expiry.</b> Every
///     <see cref="RoleNames.ExternalAuditor"/> grant MUST carry a future
///     <c>ExpiresAt</c> — external audit firms are time-boxed by contract
///     and the auto-revoke is what enforces it.</description></item>
///   <item><description><b>Site-scoped grade siteId.</b>
///     <see cref="RoleNames.SiteCashier"/> and <see cref="RoleNames.SiteSupervisor"/>
///     are site-scoped grades; every grant must carry a non-null
///     <c>siteId</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// The runtime voucher-level checks
/// (<c>approver ≠ submitter</c> / <c>approver ≠ disburser</c>) in
/// <c>PettyCashService</c> stay; those are action-level not role-level
/// and remain the second layer of defence.
/// </para>
/// </remarks>
public interface ISodService
{
    /// <summary>
    /// Validate a proposed grant. Throws <see cref="SodViolationException"/>
    /// when the grant violates an SoD invariant: audit-vs-ops, missing
    /// ExternalAuditor expiry, or missing site for a site-scoped grade.
    /// </summary>
    /// <param name="userId">The user receiving the grant.</param>
    /// <param name="newRoleName">The role being granted (must be one of <see cref="RoleNames.All"/>).</param>
    /// <param name="siteId">Site the grant is scoped to (null for HQ-wide grants).</param>
    /// <param name="expiresAt">Required for <see cref="RoleNames.ExternalAuditor"/>; ignored otherwise.</param>
    /// <param name="auditFirm">
    /// Free-text audit firm name; only meaningful when
    /// <paramref name="newRoleName"/> is <see cref="RoleNames.ExternalAuditor"/>.
    /// </param>
    Task ValidateGrantAsync(
        Guid userId,
        string newRoleName,
        Guid? siteId,
        DateTimeOffset? expiresAt = null,
        string? auditFirm = null,
        CancellationToken ct = default);

    /// <summary>
    /// Soft warnings — empty for the v2 grade hierarchy. Kept on the
    /// interface so existing call sites keep compiling; warning generation
    /// would re-emerge if a future iteration brings back grant-time
    /// concentration risk checks (e.g. SuperAdmin tenancy ceiling).
    /// </summary>
    Task<IReadOnlyList<string>> GetWarningsAsync(
        Guid userId,
        string newRoleName,
        Guid? siteId,
        CancellationToken ct = default);
}

/// <summary>
/// Thrown when a proposed role grant violates an SoD invariant. The HR
/// panel surfaces the message verbatim; runtime call sites swallow it as
/// a 4xx-style domain error.
/// </summary>
public sealed class SodViolationException : Exception
{
    public SodViolationException(string message) : base(message) { }
}

/// <summary>
/// EF-backed implementation. Every query uses <c>IgnoreQueryFilters()</c>
/// because grants are evaluated across all tenants the user belongs to —
/// the SoD posture isn't tenant-scoped.
/// </summary>
public sealed class SodService : ISodService
{
    private readonly IdentityDbContext _db;
    private readonly TimeProvider _clock;

    public SodService(IdentityDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task ValidateGrantAsync(
        Guid userId,
        string newRoleName,
        Guid? siteId,
        DateTimeOffset? expiresAt = null,
        string? auditFirm = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newRoleName);

        // Legacy / unknown role names bypass SoD — they pre-date the
        // catalogue, are NOT seeded into identity.role_permissions
        // (so resolve to ZERO permissions if granted), and are migrated
        // by HR through the inline UserDialog when convenient.
        if (!RoleNames.All.Contains(newRoleName))
        {
            return;
        }

        // ============================================================
        // 1) Site-scoped grade siteId rule.
        // ============================================================
        if (RoleNames.RequiresSite(newRoleName) && siteId is null)
        {
            throw new SodViolationException(
                $"{newRoleName} is a site-scoped grade — every grant must specify a siteId. " +
                "Pick the site from the dropdown before saving.");
        }

        // ============================================================
        // 2) ExternalAuditor expiry / firm rules.
        // ============================================================
        if (string.Equals(newRoleName, RoleNames.ExternalAuditor, StringComparison.Ordinal))
        {
            if (expiresAt is null)
            {
                throw new SodViolationException(
                    $"{RoleNames.ExternalAuditor} grants require a non-null ExpiresAt — every external-audit access must auto-revoke.");
            }
            if (expiresAt <= _clock.GetUtcNow())
            {
                throw new SodViolationException(
                    $"{RoleNames.ExternalAuditor} ExpiresAt must be in the future (received {expiresAt:O}).");
            }
            if (string.IsNullOrWhiteSpace(auditFirm))
            {
                throw new SodViolationException(
                    $"{RoleNames.ExternalAuditor} grants require a non-empty audit firm name (e.g. \"PwC Ghana\").");
            }
        }

        // ============================================================
        // 3) Audit-vs-ops exclusion.
        //    A user is on EITHER the ops ladder OR the audit ring —
        //    never both. Pull the user's existing (non-expired) grants
        //    once and check both directions.
        // ============================================================
        var now = _clock.GetUtcNow();
        var existingNames = await _db.UserRoles
            .IgnoreQueryFilters()
            .Where(ur => ur.UserId == userId && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Join(_db.Roles.IgnoreQueryFilters(),
                  ur => ur.RoleId, r => r.RoleId,
                  (ur, r) => r.Name)
            .Distinct()
            .ToListAsync(ct);

        var newIsAudit = RoleNames.IsAuditRole(newRoleName);
        var newIsOps = RoleNames.IsOpsRole(newRoleName);

        if (newIsAudit)
        {
            var conflictingOps = existingNames.FirstOrDefault(n => RoleNames.IsOpsRole(n));
            if (conflictingOps is not null)
            {
                throw new SodViolationException(
                    $"SoD: cannot grant {newRoleName} to a user who already holds the ops grade '{conflictingOps}'. " +
                    "Auditors must not operate the system they audit. Revoke the ops grade first.");
            }
        }

        if (newIsOps)
        {
            var conflictingAudit = existingNames.FirstOrDefault(n => RoleNames.IsAuditRole(n));
            if (conflictingAudit is not null)
            {
                throw new SodViolationException(
                    $"SoD: cannot grant {newRoleName} to a user who already holds the audit grade '{conflictingAudit}'. " +
                    "Auditors must not operate the system they audit. Revoke the audit grade first.");
            }
        }
    }

    /// <summary>
    /// Returns an empty list. The flat-15-role warning catalogue (ApClerk
    /// + ArClerk, FinanceController + AR/AP, etc.) is gone — composition
    /// makes those concerns moot under the concentric grade model. Kept
    /// async-shaped so the HR panel keeps compiling and so a future
    /// iteration can re-introduce grant-time warnings (e.g. break-glass
    /// ceiling for SuperAdmin) without an interface break.
    /// </summary>
    public Task<IReadOnlyList<string>> GetWarningsAsync(
        Guid userId,
        string newRoleName,
        Guid? siteId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newRoleName);
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
