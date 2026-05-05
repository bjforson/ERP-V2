using NickERP.Inspection.Core.Retention;

namespace NickERP.Inspection.Application.Retention;

/// <summary>
/// Sprint 39 — resolved retention policy for one
/// <see cref="RetentionClass"/>, ready for the
/// <c>RetentionEnforcerWorker</c> to use as the cutoff input. Returned
/// from <c>RetentionService.GetRetentionPolicyAsync</c>.
///
/// <para>
/// <b>RetentionDays.</b> The wallclock-days a closed case must persist
/// before becoming purge-eligible. <c>int.MaxValue</c> means "never
/// auto-purges" (Training, LegalHold, or any class returning the
/// fallback for a non-eligible class).
/// </para>
///
/// <para>
/// <b>IsAutoPurgeEligible.</b> Mirrors
/// <see cref="RetentionPolicyDefaults.IsAutoPurgeEligible"/> — true only
/// for <see cref="RetentionClass.Standard"/> + <see cref="RetentionClass.Extended"/>.
/// The worker narrows on this before computing the cutoff date so it
/// doesn't even build a candidate query for non-eligible classes.
/// </para>
/// </summary>
/// <param name="Class">The retention class the policy resolves for.</param>
/// <param name="RetentionDays">Wallclock days a closed case persists before purge eligibility.</param>
/// <param name="IsAutoPurgeEligible">True when the worker may surface candidates for this class.</param>
/// <param name="Source">"tenant-setting" when read from <c>tenant_settings</c>; "fallback" when the hard-coded default applied.</param>
public sealed record RetentionPolicy(
    RetentionClass Class,
    int RetentionDays,
    bool IsAutoPurgeEligible,
    string Source);
