namespace NickERP.Inspection.Application.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — per-tenant requirement enable/threshold lookup.
///
/// <para>
/// The <see cref="CompletenessChecker"/> consults this before invoking a
/// requirement. Defaults to "all requirements enabled at default
/// threshold" when no row exists; admin sets
/// <c>TenantCompletenessSetting.Enabled</c>=false to silence a noisy or
/// temporarily-broken requirement, or sets
/// <c>TenantCompletenessSetting.MinThreshold</c> to a non-null value
/// to override the requirement's built-in default threshold.
/// </para>
///
/// <para>
/// Lookups happen N times per case-evaluation (where N=registered
/// requirements) so a per-checker call cache keyed by tenantId+requirementId
/// is acceptable. The default Postgres-backed implementation in
/// <see cref="DbCompletenessRequirementProvider"/> uses a per-call
/// dictionary — each evaluation is a fresh read; cross-tenant changes
/// don't leak. Mirrors the Sprint 28
/// <c>IRuleEnablementProvider</c> pattern.
/// </para>
/// </summary>
public interface ICompletenessRequirementProvider
{
    /// <summary>
    /// True when the requirement should run for the given tenant. False
    /// when the admin disabled it. Errors (DB unreachable, etc.) MUST
    /// default to true — fail-open is acceptable for a soft signal that
    /// already degrades to Skip on missing data.
    /// </summary>
    Task<bool> IsEnabledAsync(long tenantId, string requirementId, CancellationToken ct = default);

    /// <summary>
    /// Per-tenant numeric threshold override. Null when no override row
    /// exists; the requirement uses its built-in default in that case.
    /// </summary>
    Task<decimal?> GetThresholdAsync(long tenantId, string requirementId, CancellationToken ct = default);

    /// <summary>
    /// Bulk variant — returns the disabled requirement ids for a tenant
    /// in one query plus the per-requirement threshold overrides.
    /// Used by <see cref="CompletenessChecker"/> at the top of a run to
    /// filter the requirement list once instead of per-requirement. The
    /// returned dictionary is keyed by requirement id (case-insensitive)
    /// and carries (Enabled, MinThreshold) for every override row that
    /// exists.
    /// </summary>
    Task<IReadOnlyDictionary<string, CompletenessSettingSnapshot>> GetSettingsAsync(
        long tenantId,
        CancellationToken ct = default);
}

/// <summary>
/// Sprint 31 / B5.1 — bulk-fetch shape for
/// <see cref="ICompletenessRequirementProvider.GetSettingsAsync"/>.
/// Carries the (Enabled, MinThreshold) pair for one (tenant, requirement)
/// row.
/// </summary>
public sealed record CompletenessSettingSnapshot(bool Enabled, decimal? MinThreshold);
