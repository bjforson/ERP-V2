namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 28 — per-tenant rule enable / disable lookup.
///
/// <para>
/// The <see cref="ValidationEngine"/> consults this before invoking a
/// rule. Defaults to "all rules enabled" when no row exists; the admin
/// UI sets <c>TenantValidationRuleSetting.Enabled</c>=false to
/// silence a noisy or temporarily-broken rule for a specific tenant.
/// </para>
///
/// <para>
/// Lookups happen N times per case-evaluation (where N=registered rules)
/// so a per-engine call cache keyed by tenantId+ruleId is acceptable.
/// The default Postgres-backed implementation in
/// <see cref="DbRuleEnablementProvider"/> uses a per-call dictionary —
/// each evaluation is a fresh read; cross-tenant changes don't leak.
/// </para>
/// </summary>
public interface IRuleEnablementProvider
{
    /// <summary>
    /// True when the rule should run for the given tenant. False when
    /// the admin disabled it. Errors (DB unreachable, etc.) MUST default
    /// to true — fail-open is acceptable for a soft signal that already
    /// degrades to Skip on missing data.
    /// </summary>
    Task<bool> IsEnabledAsync(long tenantId, string ruleId, CancellationToken ct = default);

    /// <summary>
    /// Bulk variant — returns the disabled rule ids for a tenant in one
    /// query. Used by <see cref="ValidationEngine"/> at the top of a
    /// run to filter the rule list once instead of per-rule. The set
    /// MUST be case-insensitive on rule id.
    /// </summary>
    Task<IReadOnlySet<string>> DisabledRuleIdsAsync(long tenantId, CancellationToken ct = default);
}
