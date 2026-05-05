namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 31 / B5.2 — per-tenant SLA budget for a named SLA window.
///
/// <para>
/// Stored in <c>tenancy.tenant_sla_settings</c> with the usual
/// <c>tenant_isolation_*</c> RLS policy. One row per (TenantId, WindowName);
/// absence of a row implies "use the engine default budget" — admins only
/// persist rows when they want to override the default for their tenant
/// (sparse storage, mirrors the
/// <see cref="TenantCompletenessSetting"/> + Sprint 28
/// <see cref="TenantValidationRuleSetting"/> patterns).
/// </para>
///
/// <para>
/// <see cref="WindowName"/> is the stable code for the SLA window —
/// dotted-lowercase by convention (e.g. <c>case.open_to_validated</c>,
/// <c>case.validated_to_verdict</c>, <c>case.verdict_to_submitted</c>).
/// The <see cref="TargetMinutes"/> defines the soft target; cases that
/// transit the window in under that time are <c>OnTime</c>. Cases that
/// exceed <see cref="TargetMinutes"/> by more than 50% (or a full
/// hour, whichever is smaller) are <c>AtRisk</c>; cases that miss by
/// more than the target trip <c>Breached</c>.
/// </para>
/// </summary>
public sealed class TenantSlaSetting : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The owning tenant. Stamped by interceptor on insert.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Stable window identifier — dotted-lowercase. Case-insensitive
    /// comparisons; stored as supplied by the SLA tracker.
    /// </summary>
    public string WindowName { get; set; } = string.Empty;

    /// <summary>
    /// Soft target in wall-clock minutes from window-open to
    /// window-close. Must be positive.
    /// </summary>
    public int TargetMinutes { get; set; }

    /// <summary>
    /// True (default) → window opens automatically on case creation +
    /// closes on terminal-state transitions; false → engine skips this
    /// window for the tenant. Lets a tenant disable a window without
    /// forgetting the configured budget.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When the row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Identity user id of the operator who flipped the row.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
