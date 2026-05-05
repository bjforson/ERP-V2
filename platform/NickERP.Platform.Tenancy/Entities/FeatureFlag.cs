namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 35 / B8.2 — per-tenant on/off feature flag. Sparse rows: a
/// missing row implies "use the default the calling code passed",
/// the same convention <see cref="TenantValidationRuleSetting"/> uses.
///
/// <para>
/// Tenant-scoped (<see cref="ITenantOwned"/>) and lives under the
/// <c>tenancy</c> schema so RLS can isolate rows by <c>app.tenant_id</c>.
/// The <c>(TenantId, FlagKey)</c> pair is unique — at most one row per
/// flag per tenant.
/// </para>
///
/// <para>
/// Flag keys are vendor-neutral lowercase dotted identifiers
/// (e.g. <c>inspection.cross_record_split.auto_resolve</c>,
/// <c>portal.launcher.show_disabled_modules</c>). The catalogue of
/// "known" keys lives in code that calls <c>IFeatureFlagService.IsEnabledAsync</c>
/// — operators surface a curated list in the admin UI plus any
/// flags that already have a row in the database.
/// </para>
/// </summary>
public sealed class FeatureFlag : ITenantOwned
{
    /// <summary>Surrogate key. Defaults via Postgres <c>gen_random_uuid()</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant. Stamped by <see cref="TenantOwnedEntityInterceptor"/> on insert.</summary>
    public long TenantId { get; set; }

    /// <summary>
    /// Vendor-neutral flag key. Conventionally lowercase, dot-separated
    /// (<c>module.feature.aspect</c>). Max 128 chars matches the other
    /// per-tenant setting tables (rule settings, completeness settings).
    /// </summary>
    public string FlagKey { get; set; } = string.Empty;

    /// <summary>
    /// Current state of the flag for this tenant. The service layer
    /// returns this when a row exists; otherwise falls back to the
    /// caller-supplied default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Wallclock of the last toggle. Refreshed on every upsert.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Identity user id of the operator who last toggled this row. Null
    /// for the seeded defaults (none today) or for system-driven flips
    /// (none today either).
    /// </summary>
    public Guid? UpdatedByUserId { get; set; }
}
