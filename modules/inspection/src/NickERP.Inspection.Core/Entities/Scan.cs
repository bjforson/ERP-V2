using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// One scanning event — a <see cref="ScannerDeviceInstance"/> capturing a
/// subject for a case. Multiple scans can hang off one case (re-scans,
/// multi-angle captures); each scan produces one or more
/// <see cref="ScanArtifact"/> rows.
/// </summary>
public sealed class Scan : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public InspectionCase? Case { get; set; }

    public Guid ScannerDeviceInstanceId { get; set; }
    public ScannerDeviceInstance? ScannerDeviceInstance { get; set; }

    /// <summary>Scanner mode at capture time (single-energy / dual-energy / IR / etc.). Free-form, adapter-defined.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>When the scan was captured (per the device clock).</summary>
    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Operator at the scanner, when known. Null = unattended capture.</summary>
    public Guid? OperatorUserId { get; set; }

    /// <summary>Idempotency key — for the source-hash of the raw artifact, lets dual-reporting scanners dedupe across pipelines.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Cross-service correlation id (often shared with the source <see cref="InspectionCase.CorrelationId"/>).</summary>
    public string? CorrelationId { get; set; }

    public long TenantId { get; set; }

    public List<ScanArtifact> Artifacts { get; set; } = new();
}
