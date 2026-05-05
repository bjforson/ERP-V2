namespace NickERP.Inspection.Core.Retention;

/// <summary>
/// Sprint 39 — vendor-neutral retention posture for inspection cases
/// + scan artifacts. Adopted from the 2026-05-05 doc-analysis (Central
/// X-Ray Image Analysis Engineering Design §9 + §13 + Annex D Table 35).
/// Drives the <c>RetentionEnforcerWorker</c>'s purge-candidate selection
/// and the <c>RetentionService</c>'s admin reclassification action.
///
/// <para>
/// <b>Vendor-neutral.</b> No Ghana customs / regime / commodity references.
/// The class is a generic posture; concrete retention windows in days are
/// resolved per-tenant via <c>ITenantSettingsService</c> with hard-coded
/// fallbacks matching the doc-analysis Table 35 ranges. Adapters or
/// country modules that need to map vendor-specific tags to a class do so
/// in their own layer; this enum stays neutral.
/// </para>
///
/// <para>
/// <b>Auto-purge eligibility.</b> Only <see cref="Standard"/> and
/// <see cref="Extended"/> are eligible for the worker's purge-candidate
/// surface; <see cref="Enforcement"/> and <see cref="Training"/> are
/// operator-driven only. <see cref="LegalHold"/> is the absolute trump
/// card — held cases never appear in purge candidates regardless of
/// class. The <c>InspectionCase.LegalHold</c> bool is consulted FIRST,
/// before the class enum.
/// </para>
///
/// <para>
/// <b>Persistence.</b> Stored as <c>integer</c> via
/// <c>HasConversion&lt;int&gt;()</c> for stable wire format. Numeric
/// gaps (10/20/30/40/100) so future intermediate states can be inserted
/// without renumbering. Default <see cref="Standard"/> matches the
/// doc-analysis "routine cleared cases" posture.
/// </para>
/// </summary>
public enum RetentionClass
{
    /// <summary>
    /// 1-5 years. Default for routine cleared inspection cases. Eligible
    /// for auto-purge once the configured retention window elapses
    /// (default 5 years per doc-analysis Table 35 fallback).
    /// </summary>
    Standard = 0,

    /// <summary>
    /// 3-7 years. High-risk or non-cleared cases that need a longer
    /// retention window than <see cref="Standard"/>. Eligible for
    /// auto-purge once the configured window elapses (default 7 years).
    /// </summary>
    Extended = 10,

    /// <summary>
    /// 7-10+ years. Confirmed enforcement actions; subject to statutory
    /// retention requirements that vary by jurisdiction. NEVER
    /// auto-purges — operator-driven release only.
    /// </summary>
    Enforcement = 20,

    /// <summary>
    /// Approved-only retention for training-image curation per
    /// doc-analysis §13. Cases (and their artifacts) tagged
    /// <see cref="Training"/> are kept until an operator explicitly
    /// releases them; NEVER auto-purges.
    /// </summary>
    Training = 30,

    /// <summary>
    /// Indefinite. Cases under investigation or litigation. WORM-style
    /// posture documented (the <c>InspectionCase.LegalHold</c> flag +
    /// audit-trailed apply/release + cascading to artifacts gives
    /// evidentiary handling sufficient for pilot). NEVER auto-purges
    /// regardless of how long the hold persists.
    ///
    /// <para>
    /// In practice, <see cref="LegalHold"/> on the class enum is a
    /// secondary marker — the primary signal is the
    /// <c>InspectionCase.LegalHold</c> bool, which is what the
    /// <c>RetentionEnforcerWorker</c> consults. The class enum
    /// surfaces "this case has historically been held" in admin views
    /// even after the bool is released; class is operator-set,
    /// transitive, durable.
    /// </para>
    /// </summary>
    LegalHold = 100
}

/// <summary>
/// Sprint 39 — fallback retention period (in days) for each
/// <see cref="RetentionClass"/>. Values match the doc-analysis Table 35
/// midpoint where the class allows a range. Per-tenant overrides land
/// in <c>tenant_settings</c> under the keys defined on
/// <see cref="RetentionPolicyDefaults"/>.
/// </summary>
public static class RetentionPolicyDefaults
{
    /// <summary>Tenant-settings key for the Standard-class retention period in days.</summary>
    public const string StandardDaysKey = "inspection.retention.standard_days";

    /// <summary>Tenant-settings key for the Extended-class retention period in days.</summary>
    public const string ExtendedDaysKey = "inspection.retention.extended_days";

    /// <summary>Tenant-settings key for the Enforcement-class retention period in days (informational; never auto-purges).</summary>
    public const string EnforcementDaysKey = "inspection.retention.enforcement_days";

    /// <summary>
    /// Fallback retention in days for <see cref="RetentionClass.Standard"/>.
    /// 5 years (= 1825 days). Hard-coded baseline so the worker has a
    /// usable default when the per-tenant override is absent.
    /// </summary>
    public const int StandardDaysFallback = 1825;

    /// <summary>
    /// Fallback retention in days for <see cref="RetentionClass.Extended"/>.
    /// 7 years (= 2555 days).
    /// </summary>
    public const int ExtendedDaysFallback = 2555;

    /// <summary>
    /// Fallback retention in days for <see cref="RetentionClass.Enforcement"/>.
    /// 10 years (= 3650 days). Informational only — Enforcement never
    /// auto-purges.
    /// </summary>
    public const int EnforcementDaysFallback = 3650;

    /// <summary>
    /// Returns the configured retention window for <paramref name="cls"/>
    /// in days. Used by <c>RetentionService.GetRetentionPolicyAsync</c>
    /// and the <c>RetentionEnforcerWorker</c>'s candidate selection
    /// query.
    /// </summary>
    /// <param name="cls">The retention class.</param>
    /// <returns>
    /// Days the case must remain closed before becoming purge-eligible.
    /// <see cref="RetentionClass.Training"/> + <see cref="RetentionClass.LegalHold"/>
    /// return <c>int.MaxValue</c> (effectively "never").
    /// </returns>
    public static int FallbackDays(RetentionClass cls) => cls switch
    {
        RetentionClass.Standard => StandardDaysFallback,
        RetentionClass.Extended => ExtendedDaysFallback,
        RetentionClass.Enforcement => EnforcementDaysFallback,
        RetentionClass.Training => int.MaxValue,
        RetentionClass.LegalHold => int.MaxValue,
        _ => StandardDaysFallback
    };

    /// <summary>
    /// True when the class is eligible for the
    /// <c>RetentionEnforcerWorker</c>'s automated purge-candidate
    /// surface; false when operator-driven release is the only path
    /// (Enforcement, Training, LegalHold).
    /// </summary>
    public static bool IsAutoPurgeEligible(RetentionClass cls) =>
        cls is RetentionClass.Standard or RetentionClass.Extended;

    /// <summary>
    /// Setting key (<see cref="StandardDaysKey"/> / <see cref="ExtendedDaysKey"/>
    /// / <see cref="EnforcementDaysKey"/>) for a class, or <c>null</c>
    /// if the class doesn't carry a configurable window
    /// (<see cref="RetentionClass.Training"/>, <see cref="RetentionClass.LegalHold"/>).
    /// </summary>
    public static string? SettingKey(RetentionClass cls) => cls switch
    {
        RetentionClass.Standard => StandardDaysKey,
        RetentionClass.Extended => ExtendedDaysKey,
        RetentionClass.Enforcement => EnforcementDaysKey,
        _ => null
    };
}
