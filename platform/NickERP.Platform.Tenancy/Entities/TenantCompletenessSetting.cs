namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 31 / B5.1 — per-tenant override flag + threshold for an
/// inspection completeness requirement.
///
/// <para>
/// Stored in <c>tenancy.tenant_completeness_settings</c> with the usual
/// <c>tenant_isolation_*</c> RLS policy (USING <c>app.tenant_id</c> =
/// TenantId). One row per (TenantId, RequirementId); absence of a row
/// implies "requirement enabled at default threshold" — admins only
/// persist rows when they want to disable a requirement or override
/// its threshold (sparse storage).
/// </para>
///
/// <para>
/// <see cref="ITenantOwned"/> so the
/// <c>TenantOwnedEntityInterceptor</c> stamps <c>TenantId</c> on insert
/// without the calling code having to set it manually. Updates flow
/// through <c>CompletenessService</c> in Inspection.Web rather than
/// generic CRUD — every flip records an audit event.
/// </para>
///
/// <para>
/// Mirrors the Sprint 28 <see cref="TenantValidationRuleSetting"/> shape.
/// SLA-budget settings can be combined into this same row when the
/// requirement carries an SLA semantic; B5 ships SLA budgets as a
/// separate <see cref="TenantSlaSetting"/> entity to keep the
/// completeness/SLA models clean for analytics queries.
/// </para>
/// </summary>
public sealed class TenantCompletenessSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The owning tenant. Stamped by interceptor on insert.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Stable requirement identifier — matches the <c>RequirementId</c>
    /// emitted by an inspection <c>ICompletenessRequirement</c> (e.g.
    /// <c>required.scan_artifact</c>). Case-insensitive comparisons;
    /// stored as-emitted by the requirement.
    /// </summary>
    public string RequirementId { get; set; } = string.Empty;

    /// <summary>
    /// True (default) → requirement runs; false → engine skips it for
    /// this tenant. Sparse rows: only persisted when the admin disables
    /// a requirement or overrides its threshold.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Optional per-tenant numeric threshold override. Interpretation is
    /// up to the requirement (e.g. minimum-scan-count requirement uses
    /// it as the count floor; minimum-image-coverage requirement reads
    /// it as a percentage). Null → use the requirement's built-in
    /// default. Stored as decimal so percent-based and count-based
    /// requirements share the same column without a discriminator.
    /// </summary>
    public decimal? MinThreshold { get; set; }

    /// <summary>When the row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Identity user id of the operator who flipped the flag.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
