using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Inspection.Application.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — Postgres-backed implementation of
/// <see cref="ICompletenessRequirementProvider"/>. Reads from
/// <c>tenancy.tenant_completeness_settings</c> via the canonical
/// <see cref="TenancyDbContext"/>; relies on the
/// <c>tenant_isolation_tenant_completeness_settings</c> RLS policy to
/// scope reads to the current tenant.
///
/// <para>
/// No caching at this layer — the checker evaluates all requirements
/// under one pass per case, so a single bulk-read query covers an
/// entire evaluation. Mirrors the Sprint 28
/// <c>DbRuleEnablementProvider</c> shape.
/// </para>
/// </summary>
public sealed class DbCompletenessRequirementProvider : ICompletenessRequirementProvider
{
    private readonly TenancyDbContext _tenancyDb;

    public DbCompletenessRequirementProvider(TenancyDbContext tenancyDb)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
    }

    public async Task<bool> IsEnabledAsync(long tenantId, string requirementId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(requirementId)) return true;
        var lower = requirementId.ToLowerInvariant();
        var row = await _tenancyDb.TenantCompletenessSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                  && s.RequirementId.ToLower() == lower,
                ct);
        return row?.Enabled ?? true;
    }

    public async Task<decimal?> GetThresholdAsync(long tenantId, string requirementId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(requirementId)) return null;
        var lower = requirementId.ToLowerInvariant();
        var row = await _tenancyDb.TenantCompletenessSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                  && s.RequirementId.ToLower() == lower,
                ct);
        return row?.MinThreshold;
    }

    public async Task<IReadOnlyDictionary<string, CompletenessSettingSnapshot>> GetSettingsAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        var rows = await _tenancyDb.TenantCompletenessSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new { s.RequirementId, s.Enabled, s.MinThreshold })
            .ToListAsync(ct);
        var dict = new Dictionary<string, CompletenessSettingSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            dict[r.RequirementId] = new CompletenessSettingSnapshot(r.Enabled, r.MinThreshold);
        }
        return dict;
    }
}

/// <summary>
/// Sprint 31 / B5.1 — in-memory <see cref="ICompletenessRequirementProvider"/>
/// for tests and bootstrapping. Defaults to "all requirements enabled,
/// no threshold overrides"; callers can disable specific requirements via
/// <see cref="Disable"/> or override thresholds via
/// <see cref="SetThreshold"/>.
/// </summary>
public sealed class InMemoryCompletenessRequirementProvider : ICompletenessRequirementProvider
{
    private readonly Dictionary<long, Dictionary<string, CompletenessSettingSnapshot>> _byTenant = new();

    private Dictionary<string, CompletenessSettingSnapshot> Bucket(long tenantId)
    {
        if (!_byTenant.TryGetValue(tenantId, out var bucket))
        {
            bucket = new Dictionary<string, CompletenessSettingSnapshot>(StringComparer.OrdinalIgnoreCase);
            _byTenant[tenantId] = bucket;
        }
        return bucket;
    }

    public void Disable(long tenantId, string requirementId)
    {
        var bucket = Bucket(tenantId);
        bucket.TryGetValue(requirementId, out var prev);
        bucket[requirementId] = new CompletenessSettingSnapshot(false, prev?.MinThreshold);
    }

    public void Enable(long tenantId, string requirementId)
    {
        var bucket = Bucket(tenantId);
        bucket.TryGetValue(requirementId, out var prev);
        bucket[requirementId] = new CompletenessSettingSnapshot(true, prev?.MinThreshold);
    }

    public void SetThreshold(long tenantId, string requirementId, decimal? threshold)
    {
        var bucket = Bucket(tenantId);
        bucket.TryGetValue(requirementId, out var prev);
        bucket[requirementId] = new CompletenessSettingSnapshot(prev?.Enabled ?? true, threshold);
    }

    public Task<bool> IsEnabledAsync(long tenantId, string requirementId, CancellationToken ct = default)
    {
        if (_byTenant.TryGetValue(tenantId, out var bucket)
            && bucket.TryGetValue(requirementId, out var snap))
            return Task.FromResult(snap.Enabled);
        return Task.FromResult(true);
    }

    public Task<decimal?> GetThresholdAsync(long tenantId, string requirementId, CancellationToken ct = default)
    {
        if (_byTenant.TryGetValue(tenantId, out var bucket)
            && bucket.TryGetValue(requirementId, out var snap))
            return Task.FromResult(snap.MinThreshold);
        return Task.FromResult<decimal?>(null);
    }

    public Task<IReadOnlyDictionary<string, CompletenessSettingSnapshot>> GetSettingsAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, CompletenessSettingSnapshot> result =
            _byTenant.TryGetValue(tenantId, out var bucket)
                ? new Dictionary<string, CompletenessSettingSnapshot>(bucket, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CompletenessSettingSnapshot>(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }
}
