using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;

namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 48 / Phase B — Postgres-backed
/// <see cref="IValidationSnapshotReader"/>. Reads from
/// <c>inspection.validation_rule_snapshots</c> via the canonical
/// <see cref="InspectionDbContext"/>; relies on the
/// <c>tenant_isolation_validation_rule_snapshots</c> RLS policy to
/// scope reads to the current tenant.
/// </summary>
public sealed class DbValidationSnapshotReader : IValidationSnapshotReader
{
    private readonly InspectionDbContext _db;

    public DbValidationSnapshotReader(InspectionDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task<IReadOnlyList<ValidationRuleSnapshot>> ListByCaseAsync(
        Guid caseId,
        CancellationToken ct = default)
    {
        return await _db.ValidationRuleSnapshots
            .AsNoTracking()
            .Where(s => s.CaseId == caseId)
            .OrderByDescending(s => s.EvaluatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ValidationRuleSnapshot>> ListByRuleAsync(
        long tenantId,
        string ruleId,
        int take = 50,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ruleId))
            return Array.Empty<ValidationRuleSnapshot>();
        if (take <= 0) take = 50;
        if (take > 200) take = 200;
        var ruleIdLower = ruleId.ToLowerInvariant();
        return await _db.ValidationRuleSnapshots
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.RuleId.ToLower() == ruleIdLower)
            .OrderByDescending(s => s.EvaluatedAt)
            .Take(take)
            .ToListAsync(ct);
    }
}
