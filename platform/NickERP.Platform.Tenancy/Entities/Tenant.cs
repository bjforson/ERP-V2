namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// One isolated instance of the platform. The default deployment is
/// tenant 1 ("Nick TC-Scan Operations"); a second customer would be
/// tenant 2, and so on. Tenant is a long instead of a Guid because it
/// shows up on every business-data row in every module — short keys
/// keep indexes cheap.
/// </summary>
/// <remarks>
/// Lives in <c>tenancy.tenants</c> in the canonical
/// <c>nickerp_platform</c> Postgres database (sibling schema to
/// <c>identity</c>).
///
/// Tenants are seeded by ops scripts and admin UI; modules read them
/// implicitly (every <see cref="ITenantOwned"/> entity carries
/// <c>TenantId</c>) but rarely query the tenants table directly.
/// </remarks>
public sealed class Tenant
{
    /// <summary>Stable primary key. Tenant 1 is reserved for the default deployment.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Stable short-code for the tenant (kebab-case, e.g. <c>nick-tc-scan</c>,
    /// <c>customer-2</c>). Used in URLs, S3 prefixes, log filters. Unique.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name shown in admin UIs and audit reports.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Free-text billing-plan label. Drives feature flags later; today informational.</summary>
    public string BillingPlan { get; set; } = "internal";

    /// <summary>IANA time-zone the tenant operates in. Used for "today" reports, payroll cut-off, etc.</summary>
    public string TimeZone { get; set; } = "Africa/Accra";

    /// <summary>Locale string for UI formatting (numbers, dates).</summary>
    public string Locale { get; set; } = "en-GH";

    /// <summary>Default currency for invoices, journal entries, payroll. ISO 4217.</summary>
    public string Currency { get; set; } = "GHS";

    /// <summary><c>false</c> = tenant suspended. Resolver rejects requests for suspended tenants.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// VP6 (locked 2026-05-02): how cases route through AnalysisServices that
    /// share locations. <see cref="CaseVisibilityModel.Shared"/> = case appears
    /// in every qualifying service (first-claim-wins on open). <see cref="CaseVisibilityModel.Exclusive"/>
    /// = case routes to exactly one service at intake (most-specific-service-wins).
    /// </summary>
    public CaseVisibilityModel CaseVisibilityModel { get; set; } = CaseVisibilityModel.Shared;

    /// <summary>
    /// VP6 (locked 2026-05-02): when true, a user can join more than one
    /// AnalysisService and see the union of cases visible to all their
    /// services. When false, the service-layer guard rejects a second
    /// membership; switching services is an admin operation.
    /// </summary>
    public bool AllowMultiServiceMembership { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The reserved id of the default tenant for single-customer deployments.</summary>
    public const long DefaultTenantId = 1L;

    /// <summary>The reserved short-code of the default tenant.</summary>
    public const string DefaultTenantCode = "nick-tc-scan";
}

/// <summary>
/// Marker every business-data entity must implement so the
/// <see cref="TenantOwnedEntityInterceptor"/> can stamp <c>TenantId</c> on
/// insert and so EF query filters can scope reads. Modules NEVER store
/// business data on entities that don't implement this interface.
/// </summary>
public interface ITenantOwned
{
    /// <summary>Owning tenant — set by the interceptor on insert if zero, never overwritten on update.</summary>
    long TenantId { get; set; }
}

/// <summary>
/// VP6 (locked 2026-05-02): tenant-configurable case-routing model
/// across <c>AnalysisService</c>s that share locations.
/// </summary>
public enum CaseVisibilityModel
{
    /// <summary>
    /// Default. A case appears in EVERY <c>AnalysisService</c> that
    /// includes the case's location in its scope. First-claim-wins when
    /// an analyst opens it (enforced via the unique partial index on
    /// <c>case_claims (CaseId) WHERE ReleasedAt IS NULL</c>).
    /// </summary>
    Shared = 0,

    /// <summary>
    /// A case routes to exactly one <c>AnalysisService</c> at intake —
    /// the "most-specific-service-wins" rule (smallest scope wins; ties
    /// broken by oldest creation). Other qualifying services don't see
    /// it.
    /// </summary>
    Exclusive = 10
}
