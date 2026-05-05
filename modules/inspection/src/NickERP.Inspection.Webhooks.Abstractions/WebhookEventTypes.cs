namespace NickERP.Inspection.Webhooks.Abstractions;

/// <summary>
/// Sprint 47 / Phase A — standard event vocabulary for outbound
/// webhooks. Mirrors external-doc §10 / Table 33; every constant is
/// stable for the life of subscribed adapters.
///
/// <para>
/// <b>All <see langword="const"/>.</b> Constants (not <see langword="static readonly"/>)
/// are baked into consumer assemblies at compile time — accidental
/// rename in the host produces a compile error in any in-tree
/// adapter, and out-of-tree adapters carry the old string verbatim
/// (so renames here are detectable, not silent). Cross-assembly
/// signed-contract safety is preserved.
/// </para>
///
/// <para>
/// <b>Mapping to audit events.</b> The <c>WebhookDispatchWorker</c>
/// inspects each new <c>audit.events</c> row's <c>EventType</c>
/// against this vocabulary; rows with non-matching event types are
/// ignored. Adapters that want a subset filter on
/// <see cref="WebhookEvent.EventType"/>.
/// </para>
///
/// <para>
/// <b>Vendor-neutral.</b> No country / authority / specific-vendor
/// strings. Authority-shaped events (e.g. customs-authority
/// declaration changes) belong in country-module plugins and never
/// surface here.
/// </para>
/// </summary>
public static class WebhookEventTypes
{
    /// <summary>
    /// A scan was detected as high-risk by the inference pipeline +
    /// passed the per-scanner threshold. Payload includes the scan
    /// id, case id, risk score, model id.
    /// </summary>
    public const string HIGH_RISK_SCAN_DETECTED = "HIGH_RISK_SCAN_DETECTED";

    /// <summary>
    /// A case has entered the InspectionRequired state (analyst pull
    /// required before customs release). Payload includes the case
    /// id + reason.
    /// </summary>
    public const string INSPECTION_REQUIRED = "INSPECTION_REQUIRED";

    /// <summary>
    /// An analyst (or automated reviewer) has rendered a verdict for
    /// a scan. Payload includes the scan id, reviewer id, verdict
    /// code, notes.
    /// </summary>
    public const string SCAN_REVIEWED = "SCAN_REVIEWED";

    /// <summary>
    /// A new <see cref="System.Guid"/> case has been created. Payload
    /// includes the case id, subject type/identifier, opening
    /// location.
    /// </summary>
    public const string CASE_CREATED = "CASE_CREATED";

    /// <summary>
    /// A gateway (edge node) has gone offline — health check
    /// transitioned from healthy to unhealthy. Payload includes the
    /// gateway id + last-seen timestamp.
    /// </summary>
    public const string GATEWAY_OFFLINE = "GATEWAY_OFFLINE";

    /// <summary>
    /// A scanner device instance has gone offline — periodic
    /// <c>TestAsync</c> failed. Payload includes the scanner instance
    /// id, type code, location, error message.
    /// </summary>
    public const string SCANNER_OFFLINE = "SCANNER_OFFLINE";

    /// <summary>
    /// The AI model drift detector has flagged a model whose recent
    /// inference distribution diverges from its training baseline
    /// past the threshold. Payload includes the model id, drift
    /// magnitude, sample count.
    /// </summary>
    public const string AI_MODEL_DRIFT_ALERT = "AI_MODEL_DRIFT_ALERT";

    /// <summary>
    /// A legal hold has been applied to one or more entities (case,
    /// scan artifact, audit subset). Payload includes the hold id,
    /// affected entities, applied-by user, reason.
    /// </summary>
    public const string LEGAL_HOLD_APPLIED = "LEGAL_HOLD_APPLIED";

    /// <summary>
    /// A previously-applied legal hold has been released. Payload
    /// includes the hold id, released-by user, reason.
    /// </summary>
    public const string LEGAL_HOLD_RELEASED = "LEGAL_HOLD_RELEASED";

    /// <summary>
    /// A scanner-threshold profile has been changed (approve / reject
    /// / activate). Payload includes the profile id, scanner type
    /// code, prior + new thresholds, actor.
    /// </summary>
    public const string THRESHOLD_CHANGED = "THRESHOLD_CHANGED";
}
