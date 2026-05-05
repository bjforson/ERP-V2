using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy.Pilot;

namespace NickERP.Portal.Services;

/// <summary>
/// Sprint 43 — portal-side implementation of
/// <see cref="IInspectionPilotProbeDataSource"/>. Reads
/// <c>InspectionDbContext</c> for the inspection-domain probe inputs
/// (<c>InspectionCase.IsSynthetic</c>, <c>OutboundSubmission.Status</c>).
/// Lives here rather than in the platform layer so the platform's
/// Tenancy.Database project does not need a (forbidden) reference to
/// Inspection.Database.
/// </summary>
/// <remarks>
/// <para>
/// All queries run under whatever <see cref="NickERP.Platform.Tenancy.ITenantContext"/>
/// is active when the dashboard refresh is invoked — the
/// <c>TenantConnectionInterceptor</c> on the inspection DbContext
/// pushes <c>app.tenant_id</c> on connection open, so RLS does the
/// scoping work; the explicit <c>WHERE TenantId = tenantId</c> filter
/// is belt-and-suspenders for the same reason
/// <c>ModuleRegistryService.GetModulesForTenantAsync</c> kept its
/// explicit filter pre-Sprint 43 / Phase D.
/// </para>
/// <para>
/// Scoped DI lifetime; the portal's Razor page (Phase C) resolves it
/// per-render and the underlying DbContext is per-request.
/// </para>
/// </remarks>
public sealed class InspectionPilotProbeDataSource : IInspectionPilotProbeDataSource
{
    private readonly InspectionDbContext _db;

    public InspectionPilotProbeDataSource(InspectionDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <inheritdoc />
    public async Task<bool> HasDecisionedRealCaseAsync(long tenantId, CancellationToken ct = default)
    {
        // A "decisioned real case" is a non-synthetic InspectionCase
        // that has at least one Verdict row. We check the case has a
        // verdict via Verdicts.Any (instead of joining on workflow
        // state) because the workflow state can be retrospectively
        // changed by retention / legal-hold actions while the verdict
        // itself is immutable proof of decision.
        return await _db.Cases
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsSynthetic)
            .Where(c => _db.Verdicts.Any(v => v.CaseId == c.Id))
            .AnyAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> HasSuccessfulOutboundSubmissionAsync(long tenantId, CancellationToken ct = default)
    {
        // Vendor-neutral — Status = "accepted" is the universal
        // signal for ICUMS / CMR / BOE / post-hoc adapters; the
        // dispatcher worker flips Status when the external system
        // returns a positive ack. LastAttemptAt not null doubles
        // as a guard against stale rows where status was preset by
        // a seeder.
        return await _db.OutboundSubmissions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId
                && s.Status == "accepted"
                && s.LastAttemptAt != null)
            .AnyAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Guid?> LatestDecisionedRealCaseIdAsync(long tenantId, CancellationToken ct = default)
    {
        return await _db.Cases
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && !c.IsSynthetic)
            .Where(c => _db.Verdicts.Any(v => v.CaseId == c.Id))
            .OrderByDescending(c => c.OpenedAt)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
    }
}
