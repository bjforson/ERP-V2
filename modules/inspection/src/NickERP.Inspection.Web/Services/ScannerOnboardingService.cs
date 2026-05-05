using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 41 / Phase A — admin service backing the
/// <c>/scanners</c> Razor page's onboarding wizard. Records the
/// 12-question vendor-survey questionnaire (Annex B Table 55) per
/// scanner-type and emits the <c>nickerp.inspection.scanner_onboarded</c>
/// audit event when an operator marks the questionnaire complete.
///
/// <para>
/// <b>Operator-driven, not gating.</b> The wizard is optional — a
/// scanner can register without filling it in; the row exists so
/// future audits and adapter bring-ups can answer "what does this
/// scanner family support?" structurally rather than digging through
/// vendor PDFs.
/// </para>
///
/// <para>
/// <b>Append-on-overwrite.</b> Recording the same field a second time
/// does NOT update the prior row; it inserts a new one. The reader
/// (<see cref="GetCurrentResponsesAsync"/>) takes the latest
/// <see cref="ScannerOnboardingResponse.RecordedAt"/> per field. This
/// keeps the questionnaire history visible for compliance review
/// without a parallel history table.
/// </para>
/// </summary>
public sealed class ScannerOnboardingService
{
    /// <summary>
    /// The 12 onboarding-questionnaire field codes per Annex B Table 55.
    /// Stable; once a code is here, it never gets renamed (the reader
    /// would orphan prior responses).
    /// </summary>
    public static readonly IReadOnlyList<OnboardingFieldDefinition> Fields = new OnboardingFieldDefinition[]
    {
        new("manufacturer_model", "Manufacturer / model / firmware", "Vendor name, scanner model, current firmware version, target workstation OS."),
        new("image_export_format", "Image export format + metadata", "Format the scanner emits (DICOS / vendor TIFF / proprietary blob); metadata sidecar shape."),
        new("api_sdk_availability", "API / SDK availability", "Vendor SDK / REST API documented and available; license terms; cost."),
        new("network_access", "Network access pattern", "Scanner network posture: airgap / vendor LAN / shared corporate / outbound-only."),
        new("image_ownership", "Image ownership / data residency", "Who owns the captured images per the vendor contract; data-residency constraints."),
        new("performance", "Performance envelope", "Throughput (containers/hour), peak burst, vendor-claimed latency for primary capture."),
        new("image_size", "Image size / resolution", "Pixels per primary view, bit depth, channels. Storage cost per scan estimate."),
        new("material_channels", "Material discrimination channels", "Single-energy / dual-energy / multi-energy; raw HE+LE channel availability."),
        new("dual_view_pairing", "Dual-view pairing geometry", "Side-view available; geometry parameters (angle, offset, registration calibration)."),
        new("time_sync", "Time synchronisation source", "How scanner time is synced to operator workstation / network — drift tolerance."),
        new("operator_identity", "Operator identity capture", "How operator identity is recorded with each scan (badge / SSO / manual)."),
        new("local_storage", "Local storage retention", "Onboard storage size, retention policy, deletion semantics, recovery window."),
        new("legal_constraints", "Legal / regulatory constraints", "Export-control / cross-border restrictions on imagery; sanitisation rules."),
    };

    private readonly InspectionDbContext _db;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly AuthenticationStateProvider? _auth;
    private readonly ILogger<ScannerOnboardingService> _logger;
    private readonly TimeProvider _clock;

    public ScannerOnboardingService(
        InspectionDbContext db,
        IEventPublisher events,
        ITenantContext tenant,
        ILogger<ScannerOnboardingService> logger,
        AuthenticationStateProvider? auth = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _events = events;
        _tenant = tenant;
        _auth = auth;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Record one questionnaire-field answer. Inserts a new row;
    /// does NOT update prior rows. The reader takes the latest
    /// <see cref="ScannerOnboardingResponse.RecordedAt"/> per field.
    /// </summary>
    public async Task RecordResponseAsync(
        string scannerDeviceTypeId,
        string fieldName,
        string value,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerDeviceTypeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentNullException.ThrowIfNull(value);

        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved — UseNickErpTenancy() must run before this admin action.");
        }

        var (actor, _) = await CurrentActorAsync();

        _db.ScannerOnboardingResponses.Add(new ScannerOnboardingResponse
        {
            Id = Guid.NewGuid(),
            ScannerDeviceTypeId = scannerDeviceTypeId.Trim(),
            FieldName = fieldName.Trim(),
            Value = value,
            RecordedAt = _clock.GetUtcNow(),
            RecordedByUserId = actor,
            TenantId = _tenant.TenantId
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Get the current ("latest per field") set of responses for the
    /// given scanner-device-type. Returns a dictionary keyed by
    /// <see cref="ScannerOnboardingResponse.FieldName"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ScannerOnboardingResponse>> GetCurrentResponsesAsync(
        string scannerDeviceTypeId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerDeviceTypeId);

        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved — UseNickErpTenancy() must run before this admin action.");
        }

        var typeCodeNorm = scannerDeviceTypeId.Trim();
        var tenantId = _tenant.TenantId;

        // Group by field, take the latest RecordedAt per field. Done
        // client-side (after AsNoTracking + ToList) so the EF in-memory
        // provider used in tests doesn't have to translate the GroupBy
        // + Order semantics.
        var rows = await _db.ScannerOnboardingResponses.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ScannerDeviceTypeId == typeCodeNorm)
            .ToListAsync(ct);

        var latestPerField = rows
            .GroupBy(r => r.FieldName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.RecordedAt).First(),
                StringComparer.Ordinal);

        return latestPerField;
    }

    /// <summary>
    /// Mark the onboarding as complete for the given scanner-device-type
    /// — emits <c>nickerp.inspection.scanner_onboarded</c> with the
    /// answered manufacturer / model / has-plugin metadata. Best-effort
    /// audit emission (warning + continue on failure; matches Sprint 28
    /// pattern). Returns the latest-per-field snapshot the wizard
    /// recorded so the caller can show a confirmation.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ScannerOnboardingResponse>> MarkOnboardingCompleteAsync(
        string scannerDeviceTypeId,
        bool hasPlugin,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scannerDeviceTypeId);

        var responses = await GetCurrentResponsesAsync(scannerDeviceTypeId, ct);
        var (actor, tenantId) = await CurrentActorAsync();

        // Pull the manufacturer/model breakdown out of the
        // questionnaire when we have it. Empty string is safe — the
        // event consumer only treats null as "not provided".
        responses.TryGetValue("manufacturer_model", out var manuRow);
        var manufacturerModel = manuRow?.Value ?? string.Empty;
        // Best-effort split: caller convention is "Manufacturer | Model |
        // Firmware | OS". We don't enforce, so split on '|' and pad.
        var parts = manufacturerModel.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var manufacturer = parts.Length > 0 ? parts[0] : string.Empty;
        var model = parts.Length > 1 ? parts[1] : string.Empty;

        try
        {
            var payload = new
            {
                tenantId,
                scannerDeviceTypeId = scannerDeviceTypeId.Trim(),
                manufacturer,
                model,
                hasPlugin,
                fieldsAnswered = responses.Count,
                completedAt = _clock.GetUtcNow()
            };
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(
                tenantId,
                "nickerp.inspection.scanner_onboarded",
                "ScannerDeviceType",
                scannerDeviceTypeId.Trim(),
                _clock.GetUtcNow());
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: actor,
                correlationId: null,
                eventType: "nickerp.inspection.scanner_onboarded",
                entityType: "ScannerDeviceType",
                entityId: scannerDeviceTypeId.Trim(),
                payload: json,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "ScannerOnboardingService failed to emit nickerp.inspection.scanner_onboarded for type={TypeCode}.",
                scannerDeviceTypeId);
        }

        return responses;
    }

    private async Task<(Guid? UserId, long TenantId)> CurrentActorAsync()
    {
        Guid? id = null;
        if (_auth is not null)
        {
            try
            {
                var state = await _auth.GetAuthenticationStateAsync();
                var idClaim = state.User?.FindFirst("nickerp:id")?.Value;
                if (Guid.TryParse(idClaim, out var g)) id = g;
            }
            catch (InvalidOperationException)
            {
                // Outside a Razor scope (workers/tests) — actor null is fine.
            }
        }

        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved — UseNickErpTenancy() must run before this admin action.");
        }
        return (id, _tenant.TenantId);
    }
}

/// <summary>
/// One field on the scanner-onboarding questionnaire (Annex B Table 55).
/// </summary>
/// <param name="FieldName">Stable code; never renamed.</param>
/// <param name="DisplayLabel">Operator-facing short label.</param>
/// <param name="HelpText">Operator-facing description (one or two sentences).</param>
public sealed record OnboardingFieldDefinition(
    string FieldName,
    string DisplayLabel,
    string HelpText);
