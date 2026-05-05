using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Inspection.Application.Sla;

/// <summary>
/// Sprint 31 / B5.1 — Postgres-backed implementation of
/// <see cref="ISlaSettingsProvider"/>. Reads from
/// <c>tenancy.tenant_sla_settings</c> via the canonical
/// <see cref="TenancyDbContext"/>; relies on the
/// <c>tenant_isolation_tenant_sla_settings</c> RLS policy to scope
/// reads to the current tenant.
/// </summary>
public sealed class DbSlaSettingsProvider : ISlaSettingsProvider
{
    private readonly TenancyDbContext _tenancyDb;

    public DbSlaSettingsProvider(TenancyDbContext tenancyDb)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
    }

    public async Task<IReadOnlyDictionary<string, SlaSettingSnapshot>> GetSettingsAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        var rows = await _tenancyDb.TenantSlaSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new { s.WindowName, s.TargetMinutes, s.Enabled })
            .ToListAsync(ct);
        var dict = new Dictionary<string, SlaSettingSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            dict[r.WindowName] = new SlaSettingSnapshot(r.Enabled, r.TargetMinutes);
        return dict;
    }
}

/// <summary>
/// Sprint 31 / B5.1 — in-memory <see cref="ISlaSettingsProvider"/> for
/// tests and bootstrapping. Defaults to "all windows enabled at engine
/// default budget"; callers can disable specific windows or override
/// budgets via <see cref="Set"/>.
/// </summary>
public sealed class InMemorySlaSettingsProvider : ISlaSettingsProvider
{
    private readonly Dictionary<long, Dictionary<string, SlaSettingSnapshot>> _byTenant = new();

    public void Set(long tenantId, string windowName, bool enabled, int targetMinutes)
    {
        if (!_byTenant.TryGetValue(tenantId, out var bucket))
        {
            bucket = new Dictionary<string, SlaSettingSnapshot>(StringComparer.OrdinalIgnoreCase);
            _byTenant[tenantId] = bucket;
        }
        bucket[windowName] = new SlaSettingSnapshot(enabled, targetMinutes);
    }

    public Task<IReadOnlyDictionary<string, SlaSettingSnapshot>> GetSettingsAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, SlaSettingSnapshot> result =
            _byTenant.TryGetValue(tenantId, out var bucket)
                ? new Dictionary<string, SlaSettingSnapshot>(bucket, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, SlaSettingSnapshot>(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }
}
