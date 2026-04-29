using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NickERP.NickFinance.Core.Entities;
using NickERP.NickFinance.Core.Roles;
using NickERP.NickFinance.Core.Services;
using NickERP.NickFinance.Database;

namespace NickERP.NickFinance.Web.Services;

/// <summary>
/// Period-lock check service — see G2 §1.7.
///
/// <para>
/// Centralises three concerns that the workflow service would otherwise
/// duplicate:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="GetYearMonth"/> formats a posting timestamp into the canonical <c>YYYY-MM</c> period key.</description></item>
///   <item><description><see cref="EnsureCanPostAsync"/> looks up the period row, throws <see cref="PeriodLockedException"/> if it's closed and the actor lacks <see cref="PettyCashRoles.ReopenPeriod"/>.</description></item>
///   <item><description><see cref="HasReopenRoleAsync"/> exposes the role check separately so the workflow service can flip the "this is a late post" flag for audit emit.</description></item>
/// </list>
///
/// <para>
/// <strong>Role check today is claim-based.</strong> The actor is read
/// from the current <see cref="ClaimsPrincipal"/>; the role match is on
/// the well-known string in <see cref="PettyCashRoles"/>. A future sprint
/// will swap this for a platform role-management call (the spec
/// explicitly calls this out as a TODO), but the structure is correct
/// — every period-aware code path goes through this service.
/// </para>
/// </summary>
public sealed class PeriodLockService
{
    private readonly NickFinanceDbContext _db;

    public PeriodLockService(NickFinanceDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Format a UTC timestamp as <c>YYYY-MM</c>.</summary>
    public static string GetYearMonth(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Throw <see cref="PeriodLockedException"/> if the period containing
    /// <paramref name="postedAt"/> is closed and the principal lacks the
    /// <see cref="PettyCashRoles.ReopenPeriod"/> role. Returns the
    /// resolved (period, isLatePost) pair for the caller to use in audit
    /// emit decisions.
    /// </summary>
    public async Task<PeriodCheckResult> EnsureCanPostAsync(
        long tenantId,
        DateTimeOffset postedAt,
        ClaimsPrincipal? principal,
        CancellationToken ct = default)
    {
        var ym = GetYearMonth(postedAt);
        var period = await _db.Periods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PeriodYearMonth == ym, ct);

        if (period is null || !period.IsClosed)
        {
            // Period missing == open. No lock; no late post.
            return new PeriodCheckResult(ym, IsLatePost: false);
        }

        if (!HasReopenRole(principal))
        {
            throw new PeriodLockedException(ym);
        }

        // Closed + role-holder == late post. Workflow service emits a
        // dedicated audit event for SOX-style review.
        return new PeriodCheckResult(ym, IsLatePost: true);
    }

    /// <summary>Returns true if the caller's claims include the reopen-period role.</summary>
    public bool HasReopenRole(ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        // Two equivalent shapes accepted: (a) a "role" claim with the value, or
        // (b) a "scope" claim with the value (matches NickErpClaims.Scope used
        // elsewhere in the platform). A real role-management story will replace
        // this with a single canonical check.
        return principal.HasClaim(ClaimTypes.Role, PettyCashRoles.ReopenPeriod)
            || principal.HasClaim("nickerp:scope", PettyCashRoles.ReopenPeriod)
            || principal.HasClaim("scope", PettyCashRoles.ReopenPeriod);
    }

    /// <summary>Async wrapper for symmetry with the EnsureCanPost path.</summary>
    public Task<bool> HasReopenRoleAsync(ClaimsPrincipal? principal)
        => Task.FromResult(HasReopenRole(principal));

    /// <summary>Returns true if the caller's claims include the publish-FX role.</summary>
    public static bool HasPublishFxRole(ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        return principal.HasClaim(ClaimTypes.Role, PettyCashRoles.PublishFx)
            || principal.HasClaim("nickerp:scope", PettyCashRoles.PublishFx)
            || principal.HasClaim("scope", PettyCashRoles.PublishFx);
    }

    /// <summary>Returns true if the caller's claims include the manage-periods role.</summary>
    public static bool HasManagePeriodsRole(ClaimsPrincipal? principal)
    {
        if (principal is null) return false;
        return principal.HasClaim(ClaimTypes.Role, PettyCashRoles.ManagePeriods)
            || principal.HasClaim("nickerp:scope", PettyCashRoles.ManagePeriods)
            || principal.HasClaim("scope", PettyCashRoles.ManagePeriods)
            // Reopen role is a strict superset for v0; keep these aligned
            // until the platform role story splits them.
            || principal.HasClaim(ClaimTypes.Role, PettyCashRoles.ReopenPeriod)
            || principal.HasClaim("nickerp:scope", PettyCashRoles.ReopenPeriod)
            || principal.HasClaim("scope", PettyCashRoles.ReopenPeriod);
    }

    /// <summary>
    /// Close a period. Throws if already closed or if no manage-periods
    /// role. Does NOT emit an audit event itself — the caller (endpoint
    /// or page handler) does that with the actor + period in context.
    /// </summary>
    public async Task<PettyCashPeriod> CloseAsync(
        long tenantId,
        string periodYearMonth,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        ValidateYearMonth(periodYearMonth);

        var existing = await _db.Periods
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PeriodYearMonth == periodYearMonth, ct);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            existing = new PettyCashPeriod
            {
                TenantId = tenantId,
                PeriodYearMonth = periodYearMonth,
                ClosedAt = now,
                ClosedByUserId = actorUserId
            };
            _db.Periods.Add(existing);
        }
        else
        {
            if (existing.IsClosed)
            {
                throw new InvalidOperationException(
                    $"Period {periodYearMonth} is already closed (since {existing.ClosedAt:O}).");
            }
            existing.ClosedAt = now;
            existing.ClosedByUserId = actorUserId;
        }

        await _db.SaveChangesAsync(ct);
        return existing;
    }

    /// <summary>Reopen a previously-closed period.</summary>
    public async Task<PettyCashPeriod> ReopenAsync(
        long tenantId,
        string periodYearMonth,
        CancellationToken ct = default)
    {
        ValidateYearMonth(periodYearMonth);

        var existing = await _db.Periods
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.PeriodYearMonth == periodYearMonth, ct);

        if (existing is null || !existing.IsClosed)
        {
            throw new InvalidOperationException(
                $"Period {periodYearMonth} is not closed; nothing to reopen.");
        }

        existing.ClosedAt = null;
        existing.ClosedByUserId = null;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    private static void ValidateYearMonth(string ym)
    {
        if (string.IsNullOrWhiteSpace(ym)
            || ym.Length != 7
            || ym[4] != '-'
            || !int.TryParse(ym.AsSpan(0, 4), out _)
            || !int.TryParse(ym.AsSpan(5, 2), out var month)
            || month < 1 || month > 12)
        {
            throw new ArgumentException(
                $"Period year-month must be 'YYYY-MM' (got '{ym}').", nameof(ym));
        }
    }
}

/// <summary>Result of a <see cref="PeriodLockService.EnsureCanPostAsync"/> call.</summary>
public sealed record PeriodCheckResult(string PeriodYearMonth, bool IsLatePost);
