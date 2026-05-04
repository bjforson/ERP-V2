using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Database;

namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 28 — Postgres-backed implementation of
/// <see cref="IRuleEnablementProvider"/>. Reads from
/// <c>tenancy.tenant_validation_rule_settings</c> via the canonical
/// <see cref="TenancyDbContext"/>; relies on the
/// <c>tenant_isolation_tenant_validation_rule_settings</c> RLS policy
/// to scope reads to the current tenant.
///
/// <para>
/// No caching at this layer — the engine evaluates all rules under one
/// pass per case, so a single pull-disabled-set query covers an entire
/// evaluation. If high-volume callers (background workers fanning out
/// across cases) emerge later, fronting this with an
/// <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/> +
/// LISTEN/NOTIFY invalidation is a small change — the contract stays
/// the same.
/// </para>
/// </summary>
public sealed class DbRuleEnablementProvider : IRuleEnablementProvider
{
    private readonly TenancyDbContext _tenancyDb;

    public DbRuleEnablementProvider(TenancyDbContext tenancyDb)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
    }

    public async Task<bool> IsEnabledAsync(long tenantId, string ruleId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ruleId)) return true;
        var row = await _tenancyDb.TenantValidationRuleSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                  && EF.Functions.ILike(s.RuleId, ruleId),
                ct);
        return row?.Enabled ?? true;
    }

    public async Task<IReadOnlySet<string>> DisabledRuleIdsAsync(long tenantId, CancellationToken ct = default)
    {
        var disabled = await _tenancyDb.TenantValidationRuleSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && !s.Enabled)
            .Select(s => s.RuleId)
            .ToListAsync(ct);
        return new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Sprint 28 — in-memory <see cref="IRuleEnablementProvider"/> for tests
/// and bootstrapping. Defaults to "all rules enabled"; callers can
/// disable specific rules via <see cref="Disable"/>.
/// </summary>
public sealed class InMemoryRuleEnablementProvider : IRuleEnablementProvider
{
    private readonly Dictionary<long, HashSet<string>> _disabled = new();

    public void Disable(long tenantId, string ruleId)
    {
        if (!_disabled.TryGetValue(tenantId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _disabled[tenantId] = set;
        }
        set.Add(ruleId);
    }

    public void Enable(long tenantId, string ruleId)
    {
        if (_disabled.TryGetValue(tenantId, out var set))
            set.Remove(ruleId);
    }

    public Task<bool> IsEnabledAsync(long tenantId, string ruleId, CancellationToken ct = default)
    {
        var off = _disabled.TryGetValue(tenantId, out var set) && set.Contains(ruleId);
        return Task.FromResult(!off);
    }

    public Task<IReadOnlySet<string>> DisabledRuleIdsAsync(long tenantId, CancellationToken ct = default)
    {
        IReadOnlySet<string> result = _disabled.TryGetValue(tenantId, out var set)
            ? new HashSet<string>(set, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(result);
    }
}
