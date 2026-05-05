namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 35 / B8.2 — generic per-tenant key/value setting. Same
/// shape as <see cref="FeatureFlag"/> but with a string value instead
/// of a boolean — meant for any tenant-scoped configuration that
/// doesn't justify its own typed entity (default SLA budgets,
/// retention windows, comms-gateway endpoints, etc.).
///
/// <para>
/// Tenant-scoped (<see cref="ITenantOwned"/>) and lives under the
/// <c>tenancy</c> schema so RLS can isolate rows by <c>app.tenant_id</c>.
/// The <c>(TenantId, SettingKey)</c> pair is unique — at most one row
/// per key per tenant.
/// </para>
///
/// <para>
/// Setting keys follow the same vendor-neutral lowercase dotted
/// convention as feature flags. Examples documented under
/// <c>docs/runbooks/12-comms-gateway-settings.md</c>:
/// <c>comms.email.smtp_host</c>, <c>comms.email.smtp_port</c>,
/// <c>comms.email.from_address</c>, <c>inspection.sla.default_budget_minutes</c>.
/// </para>
///
/// <para>
/// Value is freeform text — typed parsing is the caller's
/// responsibility. The service exposes a <c>GetIntAsync</c> helper that
/// returns a fallback when the row is missing or the parse fails so the
/// happy path stays terse.
/// </para>
/// </summary>
public sealed class TenantSetting : ITenantOwned
{
    /// <summary>Surrogate key. Defaults via Postgres <c>gen_random_uuid()</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant. Stamped by <see cref="TenantOwnedEntityInterceptor"/> on insert.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Vendor-neutral setting key. Lowercase + dot-separated, max 128
    /// chars (matches <see cref="FeatureFlag.FlagKey"/>).
    /// </summary>
    public string SettingKey { get; set; } = string.Empty;

    /// <summary>
    /// Setting value. <c>text</c> — no length cap so SMTP hostnames /
    /// JSON snippets / multi-line PEM material can all live here. Empty
    /// string is allowed (distinguishable from "not set" via the row's
    /// presence vs absence). Null is not allowed.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Wallclock of the last upsert.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Identity user id of the operator who last edited the row.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
