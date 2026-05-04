using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickHR.WebApp.Services;

/// <summary>
/// HR-side bridge to the cross-app <c>identity.users</c> store. NickHR is
/// the system-of-record for which humans exist; this service is the seam
/// where HR's create/edit flows seed (and maintain) the canonical
/// <see cref="User"/> row that NickFinance and other consumers look up
/// on every authenticated request.
/// </summary>
/// <remarks>
/// W3B (2026-04-29) — extracted as part of the "HR is system-of-record"
/// rebuild. Before this, NickFinance lazy-created a <see cref="User"/>
/// row on first CF Access login. After W3B, NickFinance's factory is
/// look-up-only and HR is the only path that creates rows. The bridge
/// key is the lower-cased email — that's the one identifier present on
/// both sides before the user has ever logged in via Cloudflare Access
/// (CF Access populates <see cref="User.CfAccessSub"/> on first login).
/// All operations are idempotent so a re-run after a partial failure is
/// safe.
/// </remarks>
public interface IIdentityProvisioningService
{
    /// <summary>
    /// Idempotent upsert of an employee into <c>identity.users</c>.
    /// Returns the canonical <c>internal_user_id</c>. Safe to call from
    /// EmployeeCreate, EmployeeEdit, or a backfill loop. Email is
    /// lower-cased before any lookup or insert.
    /// </summary>
    /// <param name="email">Work email — keyed lower-cased.</param>
    /// <param name="displayName">Human-readable name (e.g. "Akosua Mensah").</param>
    /// <param name="tenantId">Tenant id; use <c>1</c> for the default Nick TC-Scan tenant.</param>
    Task<Guid> ProvisionEmployeeAsync(string email, string displayName, long tenantId, CancellationToken ct = default);

    /// <summary>
    /// Grant a NickFinance role to a user. Role names come from
    /// <see cref="NickHR.WebApp.Identity.RoleNames"/> (the 15-role
    /// catalog: SiteCashier, SiteCustodian, SiteApprover, SiteManager,
    /// ApClerk, ApManager, ArClerk, ArManager, ArCashier, TreasuryOfficer,
    /// FinanceController, GraComplianceOfficer, InternalAuditor,
    /// ExternalAuditor, PlatformAdmin). Idempotent on
    /// <c>(user_id, role_id, site_id)</c> — a duplicate grant is a no-op
    /// and refreshes the expiry only when the new <paramref name="expiresAt"/>
    /// is later than the existing one.
    /// </summary>
    /// <remarks>
    /// W3B Phase 2 (2026-04-29) added <paramref name="auditFirm"/> to the
    /// signature for the <c>ExternalAuditor</c> role. Both
    /// <paramref name="auditFirm"/> and <paramref name="expiresAt"/> are
    /// REQUIRED when <paramref name="roleName"/> is <c>ExternalAuditor</c>
    /// — the call throws <see cref="ArgumentException"/> otherwise. For
    /// every other role both fields are ignored.
    /// </remarks>
    Task GrantRoleAsync(
        Guid userId,
        string roleName,
        Guid? siteId,
        Guid grantedByUserId,
        DateTimeOffset? expiresAt,
        string? auditFirm = null,
        CancellationToken ct = default);

    /// <summary>Revoke a previously-granted role. No-op if the grant doesn't exist.</summary>
    Task RevokeRoleAsync(Guid userId, string roleName, Guid? siteId, CancellationToken ct = default);

    /// <summary>List the user's current (non-expired) role grants.</summary>
    Task<IReadOnlyList<RoleGrant>> ListRolesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Set / replace the user's primary E.164 phone (used by the WhatsApp
    /// approval resolver). Removes any other phone rows for the user so
    /// "primary" stays single. Pass <c>null</c> or empty to clear the
    /// phone entirely.
    /// </summary>
    Task SetPrimaryPhoneAsync(Guid userId, string? phoneE164, CancellationToken ct = default);

    /// <summary>
    /// Look up the canonical <c>internal_user_id</c> for an email, or
    /// <c>null</c> if no row exists. Email is lower-cased.
    /// </summary>
    Task<Guid?> GetUserIdByEmailAsync(string email, long tenantId, CancellationToken ct = default);

    /// <summary>Get the full <see cref="User"/> row by id, or <c>null</c>.</summary>
    Task<User?> GetUserAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Get the user's primary phone, or <c>null</c> if none.</summary>
    Task<string?> GetPrimaryPhoneAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// Read-only DTO returned by <see cref="IIdentityProvisioningService.ListRolesAsync"/>.
/// </summary>
/// <remarks>
/// W3B Phase 2 added <see cref="AuditFirm"/>. It is non-null only for
/// <c>ExternalAuditor</c> grants; every other role hands back
/// <see langword="null"/>. Until Phase 1's schema migration lands the
/// column on <c>UserRole</c>, this field is always <see langword="null"/>
/// regardless of role.
/// </remarks>
public sealed record RoleGrant(
    string RoleName,
    Guid? SiteId,
    DateTimeOffset GrantedAt,
    DateTimeOffset? ExpiresAt,
    string? AuditFirm = null);

/// <summary>
/// EF-backed implementation hitting the shared <c>identity</c> schema.
/// All operations bypass the tenant query filter when looking up rows
/// because the HR app's own tenant resolution may not have caught up
/// yet (e.g. during the very first call from a freshly-loaded admin
/// circuit). Inserts always carry an explicit tenant id.
/// </summary>
public sealed class IdentityProvisioningService : IIdentityProvisioningService
{
    private readonly IdentityDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<IdentityProvisioningService> _log;

    public IdentityProvisioningService(
        IdentityDbContext db,
        ILogger<IdentityProvisioningService> log,
        TimeProvider? clock = null)
    {
        _db = db;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Guid> ProvisionEmployeeAsync(string email, string displayName, long tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var lc = email.Trim().ToLowerInvariant();

        // Match by email + tenant — CF Access sub is unknown until first login.
        var existing = await _db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == lc && u.TenantId == tenantId, ct);

        if (existing is not null)
        {
            // Refresh display name if HR edited it; everything else is stable.
            if (!string.Equals(existing.DisplayName, displayName, StringComparison.Ordinal))
            {
                existing.DisplayName = displayName;
                await _db.SaveChangesAsync(ct);
            }
            return existing.InternalUserId;
        }

        var now = _clock.GetUtcNow();
        var row = new User
        {
            CfAccessSub = null, // populated by NickFinance's factory on first CF Access login
            Email = lc,
            DisplayName = displayName,
            Status = UserStatus.Active,
            CreatedAt = now,
            LastSeenAt = null,
            TenantId = tenantId,
        };
        _db.Users.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Race: another circuit just inserted the same email. Reload and return its id.
            _log.LogWarning(ex, "Race inserting identity row for {Email}; reloading.", lc);
            _db.Entry(row).State = EntityState.Detached;
            var reloaded = await _db.Users
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(u => u.Email == lc && u.TenantId == tenantId, ct);
            if (reloaded is null) throw;
            return reloaded.InternalUserId;
        }
        return row.InternalUserId;
    }

    public async Task GrantRoleAsync(
        Guid userId,
        string roleName,
        Guid? siteId,
        Guid grantedByUserId,
        DateTimeOffset? expiresAt,
        string? auditFirm = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);

        // W3B Phase 2: ExternalAuditor REQUIRES both an expiry and the
        // audit firm name. Other roles must NOT carry an audit firm (it
        // would silently mis-tag the audit trail). We validate here so the
        // database never sees an invalid combination, regardless of which
        // call site (HR form, bootstrap CLI, REST endpoint) provisioned it.
        var isExternalAuditor = string.Equals(
            roleName,
            NickHR.WebApp.Identity.RoleNames.ExternalAuditor,
            StringComparison.Ordinal);
        if (isExternalAuditor)
        {
            if (expiresAt is null)
            {
                throw new ArgumentException(
                    "ExternalAuditor role grants must specify ExpiresAt.",
                    nameof(expiresAt));
            }
            if (string.IsNullOrWhiteSpace(auditFirm))
            {
                throw new ArgumentException(
                    "ExternalAuditor role grants must specify the audit firm name.",
                    nameof(auditFirm));
            }
        }
        else if (!string.IsNullOrWhiteSpace(auditFirm))
        {
            throw new ArgumentException(
                $"AuditFirm only applies to the ExternalAuditor role; got '{roleName}'.",
                nameof(auditFirm));
        }

        // Phase 2 (2026-04-30): defence-in-depth audit-vs-ops exclusion.
        // The HR access section UI already prevents picking both an ops
        // grade and an audit grade simultaneously, but service-level
        // enforcement protects against direct API callers and drift.
        var newIsAudit = NickHR.WebApp.Identity.RoleNames.IsAuditRole(roleName);
        var newIsOps = NickHR.WebApp.Identity.RoleNames.IsOpsRole(roleName);
        if (newIsAudit || newIsOps)
        {
            var now = _clock.GetUtcNow();
            var existingNames = await _db.UserRoles
                .Where(ur => ur.UserId == userId && (ur.ExpiresAt == null || ur.ExpiresAt > now))
                .Join(_db.Roles, ur => ur.RoleId, r => r.RoleId, (ur, r) => r.Name)
                .ToListAsync(ct);

            if (newIsAudit)
            {
                var conflict = existingNames.FirstOrDefault(NickHR.WebApp.Identity.RoleNames.IsOpsRole);
                if (conflict is not null && !string.Equals(conflict, roleName, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Cannot grant audit role '{roleName}' to a user who already holds the ops grade '{conflict}'. " +
                        "Auditors must not operate the system they audit. Revoke the ops grade first.",
                        nameof(roleName));
                }
            }
            if (newIsOps)
            {
                var conflict = existingNames.FirstOrDefault(NickHR.WebApp.Identity.RoleNames.IsAuditRole);
                if (conflict is not null && !string.Equals(conflict, roleName, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Cannot grant ops grade '{roleName}' to a user who already holds the audit role '{conflict}'. " +
                        "Auditors must not operate the system they audit. Revoke the audit role first.",
                        nameof(roleName));
                }
            }
        }

        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct)
            ?? throw new InvalidOperationException(
                $"Role '{roleName}' is not seeded in identity.roles. Run the NickFinance bootstrap CLI to seed.");

        var existing = await _db.UserRoles.FirstOrDefaultAsync(
            ur => ur.UserId == userId && ur.RoleId == role.RoleId && ur.SiteId == siteId, ct);
        if (existing is not null)
        {
            var changed = false;
            // Idempotent: only refresh expiry if the new one is longer (or removes a previous expiry).
            if (expiresAt is not null && (existing.ExpiresAt is null || existing.ExpiresAt < expiresAt))
            {
                existing.ExpiresAt = expiresAt;
                changed = true;
            }
            // W3B Phase 2: refresh the audit firm on the existing row if HR
            // changed it (e.g. firm rotated mid-engagement). Best-effort —
            // Phase 1 will land the column on UserRole; until then this is
            // a reflection-driven set that no-ops if the property doesn't
            // exist yet on the entity.
            if (isExternalAuditor)
            {
                if (TrySetAuditFirm(existing, auditFirm))
                {
                    changed = true;
                }
            }
            if (changed) await _db.SaveChangesAsync(ct);
            return;
        }

        var row = new UserRole
        {
            UserId = userId,
            RoleId = role.RoleId,
            SiteId = siteId,
            GrantedByUserId = grantedByUserId,
            GrantedAt = _clock.GetUtcNow(),
            ExpiresAt = expiresAt,
        };
        if (isExternalAuditor)
        {
            TrySetAuditFirm(row, auditFirm);
        }
        _db.UserRoles.Add(row);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Best-effort assignment of <c>UserRole.AuditFirm</c>. Phase 1 will
    /// add the column + property to <see cref="UserRole"/>; until that
    /// migration lands the property doesn't exist and we no-op silently.
    /// </summary>
    private static bool TrySetAuditFirm(UserRole row, string? auditFirm)
    {
        var prop = typeof(UserRole).GetProperty("AuditFirm");
        if (prop is null || !prop.CanWrite) return false;
        var current = prop.GetValue(row) as string;
        if (string.Equals(current, auditFirm, StringComparison.Ordinal)) return false;
        prop.SetValue(row, auditFirm);
        return true;
    }

    public async Task RevokeRoleAsync(Guid userId, string roleName, Guid? siteId, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Name == roleName, ct);
        if (role is null) return;

        var grant = await _db.UserRoles.FirstOrDefaultAsync(
            ur => ur.UserId == userId && ur.RoleId == role.RoleId && ur.SiteId == siteId, ct);
        if (grant is null) return;

        _db.UserRoles.Remove(grant);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RoleGrant>> ListRolesAsync(Guid userId, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();

        // Project to a struct that always contains a (possibly-empty) audit
        // firm so consumers can pre-populate the ExternalAuditor section.
        // We pull the entity rows then materialize the projection client-
        // side; this lets us safely call TryGetAuditFirm via reflection
        // without forcing EF to translate it into SQL.
        var pairs = await _db.UserRoles
            .Where(ur => ur.UserId == userId && (ur.ExpiresAt == null || ur.ExpiresAt > now))
            .Join(_db.Roles, ur => ur.RoleId, r => r.RoleId,
                (ur, r) => new { Row = ur, RoleName = r.Name })
            .ToListAsync(ct);

        var result = new List<RoleGrant>(pairs.Count);
        foreach (var p in pairs)
        {
            result.Add(new RoleGrant(
                p.RoleName,
                p.Row.SiteId,
                p.Row.GrantedAt,
                p.Row.ExpiresAt,
                TryGetAuditFirm(p.Row)));
        }
        return result;
    }

    private static string? TryGetAuditFirm(UserRole row)
    {
        var prop = typeof(UserRole).GetProperty("AuditFirm");
        if (prop is null || !prop.CanRead) return null;
        return prop.GetValue(row) as string;
    }

    public async Task SetPrimaryPhoneAsync(Guid userId, string? phoneE164, CancellationToken ct = default)
    {
        // Clear the existing rows; the WhatsApp resolver picks the most-recent
        // one anyway, but we keep the table tidy so "primary" is unambiguous.
        var existing = await _db.UserPhones
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            _db.UserPhones.RemoveRange(existing);
        }

        var trimmed = phoneE164?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            // Normalise local Ghana formats to E.164 before validation. HR
            // staff naturally type "0241234567" or "0207536218"; storage and
            // the WhatsApp Cloud API both want "+233241234567". Anything that
            // already looks E.164 is passed through unchanged so foreign
            // (non-Ghana) numbers also work.
            trimmed = NormaliseGhanaPhoneToE164(trimmed);
            if (!IsLooselyE164(trimmed))
            {
                throw new ArgumentException(
                    $"Phone '{phoneE164}' is not a recognised number. Enter Ghana mobile (e.g. 0241234567) or international format (e.g. +233241234567).",
                    nameof(phoneE164));
            }
            _db.UserPhones.Add(new UserPhone
            {
                UserId = userId,
                PhoneE164 = trimmed,
                Verified = false, // verification flow not yet shipped — see IdentityApproverPhoneResolver
                CreatedAt = _clock.GetUtcNow(),
            });
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<Guid?> GetUserIdByEmailAsync(string email, long tenantId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var lc = email.Trim().ToLowerInvariant();
        return await _db.Users
            .IgnoreQueryFilters()
            .Where(u => u.Email == lc && u.TenantId == tenantId)
            .Select(u => (Guid?)u.InternalUserId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<User?> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.Users
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.InternalUserId == userId, ct);
    }

    public async Task<string?> GetPrimaryPhoneAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.UserPhones
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.Verified)
            .ThenByDescending(p => p.CreatedAt)
            .Select(p => p.PhoneE164)
            .FirstOrDefaultAsync(ct);
    }

    private static bool IsLooselyE164(string s)
    {
        if (s.Length < 5 || s.Length > 32) return false;
        if (s[0] != '+') return false;
        for (var i = 1; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// Accepts the formats HR staff actually type and converts them to E.164.
    /// Already-E.164 input is passed through unchanged so non-Ghana numbers
    /// keep working. Ghana-specific shortcuts:
    /// <list type="bullet">
    ///   <item>"0241234567"  → "+233241234567"  (10 digits, leading 0)</item>
    ///   <item>"241234567"   → "+233241234567"  (9 digits, no leading 0 — common copy-paste from contacts)</item>
    ///   <item>"233241234567"→ "+233241234567"  (12 digits, no plus — copy-paste from another system)</item>
    ///   <item>"+233241234567"→ unchanged       (already E.164)</item>
    ///   <item>"+1 555 123 4567" → "+15551234567" (foreign E.164 with whitespace stripped)</item>
    /// </list>
    /// Anything else falls through unchanged; the strict E.164 check that follows
    /// will reject it with a friendly error.
    /// </summary>
    internal static string NormaliseGhanaPhoneToE164(string raw)
    {
        // Strip whitespace, parens, dashes, dots — leave + and digits.
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c == '+' || char.IsDigit(c)) sb.Append(c);
        }
        var s = sb.ToString();
        if (s.Length == 0) return raw;

        // Already E.164 → return as-is.
        if (s[0] == '+') return s;

        // 12 digits starting with 233 → prepend +.
        if (s.Length == 12 && s.StartsWith("233")) return "+" + s;

        // 10 digits starting with 0 → Ghana mobile, swap 0 for +233.
        if (s.Length == 10 && s[0] == '0') return "+233" + s.Substring(1);

        // 9 digits → Ghana mobile without leading 0.
        if (s.Length == 9) return "+233" + s;

        // Otherwise return cleaned-but-untransformed; the E.164 check rejects.
        return s;
    }
}
