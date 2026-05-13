using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Detection;

/// <summary>
/// Runs registered cross-record detectors and persists their findings.
/// </summary>
public sealed class CrossRecordDetectionService
{
    private readonly InspectionDbContext _db;
    private readonly IEnumerable<ICrossRecordScanDetector> _detectors;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ILogger<CrossRecordDetectionService> _logger;

    public CrossRecordDetectionService(
        InspectionDbContext db,
        IEnumerable<ICrossRecordScanDetector> detectors,
        ITenantContext tenant,
        IEventPublisher events,
        ILogger<CrossRecordDetectionService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _detectors = detectors ?? throw new ArgumentNullException(nameof(detectors));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Run every registered detector against the case and upsert any
    /// positive findings. Confirmed, dismissed, and split rows are left
    /// immutable on re-detection.
    /// </summary>
    public async Task<IReadOnlyList<CrossRecordDetection>> ScanAndPersistAsync(
        Guid caseId,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Cross-record detection cannot run without a resolved tenant context.");
        }

        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;
        var touched = new List<CrossRecordDetection>();

        foreach (var detector in _detectors)
        {
            CrossRecordDetectionDescriptor? descriptor;
            try
            {
                descriptor = await detector.DetectAsync(caseId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Detector {Version} threw on case {CaseId}; skipping.",
                    detector.DetectorVersion,
                    caseId);
                continue;
            }

            if (descriptor is null)
            {
                continue;
            }

            var existing = await _db.CrossRecordDetections.FirstOrDefaultAsync(
                    d => d.CaseId == caseId && d.DetectorVersion == detector.DetectorVersion,
                    ct)
                .ConfigureAwait(false);

            var subjectsJson = JsonSerializer.Serialize(descriptor.Subjects);
            if (existing is null)
            {
                existing = new CrossRecordDetection
                {
                    Id = Guid.NewGuid(),
                    CaseId = caseId,
                    DetectorVersion = detector.DetectorVersion,
                    DetectedAt = now,
                    State = CrossRecordDetectionState.Pending,
                    DetectedSubjectsJson = subjectsJson,
                    Notes = descriptor.Rationale,
                    TenantId = tenantId
                };
                _db.CrossRecordDetections.Add(existing);
            }
            else if (existing.State == CrossRecordDetectionState.Pending)
            {
                existing.DetectedAt = now;
                existing.DetectedSubjectsJson = subjectsJson;
                existing.Notes = descriptor.Rationale;
            }

            touched.Add(existing);
        }

        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (touched.Count > 0)
        {
            await EmitScannedEventAsync(caseId, tenantId, touched.Count, now, ct).ConfigureAwait(false);
        }

        return touched;
    }

    private async Task EmitScannedEventAsync(
        Guid caseId,
        long tenantId,
        int rowCount,
        DateTimeOffset occurredAt,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                caseId,
                rowCount
            });
            var key = IdempotencyKey.ForEntityChange(
                tenantId,
                "inspection.cross_record_detection.scanned",
                "InspectionCase",
                caseId.ToString(),
                occurredAt);
            var evt = DomainEvent.Create(
                tenantId,
                actorUserId: null,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: "inspection.cross_record_detection.scanned",
                entityType: "InspectionCase",
                entityId: caseId.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to emit cross_record_detection.scanned for case {CaseId}.",
                caseId);
        }
    }
}
