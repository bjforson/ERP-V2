namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 28 — per-tenant override flag for an inspection
/// <c>IValidationRule</c>.
///
/// <para>
/// Stored in <c>tenancy.tenant_validation_rule_settings</c> with the
/// usual <c>tenant_isolation_*</c> RLS policy (USING <c>app.tenant_id</c>
/// = TenantId). One row per (TenantId, RuleId); absence of a row =
/// "rule is enabled by default" — the admin only persists rows for the
/// rules they want to disable (sparse storage).
/// </para>
///
/// <para>
/// <see cref="ITenantOwned"/> so the
/// <c>TenantOwnedEntityInterceptor</c> stamps <c>TenantId</c> on insert
/// without the calling code having to set it manually. Updates flow
/// through <c>RulesAdminService</c> in Inspection.Web rather than
/// generic CRUD — every flip records an audit event.
/// </para>
/// </summary>
public sealed class TenantValidationRuleSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The owning tenant. Stamped by interceptor on insert.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Stable rule identifier — matches the <c>RuleId</c> emitted by an
    /// inspection <c>IValidationRule</c> (e.g. <c>customsgh.port_match</c>).
    /// Case-insensitive comparisons; stored as-emitted by the rule.
    /// </summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>
    /// True (default) → rule runs; false → engine skips it for this
    /// tenant. Sparse rows: only persisted when the admin disables a
    /// rule, so a missing row implies <c>Enabled=true</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When the row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Identity user id of the operator who flipped the flag.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
