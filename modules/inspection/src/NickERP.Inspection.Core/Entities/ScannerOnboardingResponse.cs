using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 41 / Phase A — one row per onboarding-questionnaire field per
/// scanner-device-type (FS6000, ASE, Nuctech, …). Captures the structured
/// vendor-survey metadata that doc-analysis Annex B Table 55 designates
/// as the standard onboarding template for every new scanner family.
///
/// <para>
/// <b>Operator-driven, not gating.</b> Filling in the questionnaire does
/// NOT block scanner registration; it's metadata captured for compliance
/// + future adapter authoring. A scanner adapter can land in the plugin
/// folder without an onboarding row, and a registered scanner can run
/// without one — the row exists so future audits and adapter bring-ups
/// have a single place to look up "what does this scanner support?".
/// </para>
///
/// <para>
/// <b>Append-only.</b> One row per (TenantId, ScannerDeviceTypeId,
/// FieldName) is the typical state, but re-recording overwrites by
/// inserting a new row with the same key — the
/// <see cref="ScannerOnboardingService.GetCurrentResponsesAsync"/>
/// reader takes the latest <see cref="RecordedAt"/> per field. This
/// keeps the questionnaire history visible for compliance review without
/// a parallel history table.
/// </para>
///
/// <para>
/// <see cref="ScannerDeviceTypeId"/> is a string TypeCode ("fs6000",
/// "ase", "nuctech-mt-1213", …) — same shape as
/// <see cref="ScannerDeviceInstance.TypeCode"/>. v2 has no separate
/// ScannerDeviceType table yet; the type-code IS the identifier. If a
/// v3 introduces a normalized type table this column becomes a Guid FK;
/// the migration is straightforward.
/// </para>
/// </summary>
public sealed class ScannerOnboardingResponse : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Stable scanner-type code — matches <see cref="ScannerDeviceInstance.TypeCode"/>
    /// and the <c>[Plugin(typeCode)]</c> on the adapter class.
    /// </summary>
    public string ScannerDeviceTypeId { get; set; } = string.Empty;

    /// <summary>
    /// Questionnaire field name. One of the 12 Annex-B field codes
    /// (e.g. <c>manufacturer_model</c>, <c>image_export_format</c>,
    /// <c>api_sdk_availability</c>, <c>network_access</c>, etc.).
    /// </summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>The operator's answer for this field. Free-form; text column.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>When the operator recorded this answer.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The operator who recorded this answer. Null for system-seeded rows.</summary>
    public Guid? RecordedByUserId { get; set; }

    public long TenantId { get; set; }
}
