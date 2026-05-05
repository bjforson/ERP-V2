using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.Detection;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 31 / B5.2 — admin-side service backing the
/// <c>/admin/cross-record-scans</c> Razor page.
///
/// <para>
/// Reads detected multi-subject cases (<see cref="CrossRecordDetection"/>),
/// drives the analyst confirm/dismiss/split workflow, and persists
/// the lifecycle transitions with audit events. The detector itself
/// is read-only (Sprint 28's
/// <see cref="ICrossRecordScanDetector"/>); this service owns the
/// state-machine + the audit trail.
/// </para>
///
/// <para>
/// <b>Idempotent re-detection</b>. <see cref="ScanAndPersistAsync"/>
/// upserts the (CaseId, DetectorVersion) row — a re-run sees the
/// existing row and refreshes <see cref="CrossRecordDetection.DetectedAt"/>
/// + the JSON payload without losing any analyst notes already
/// captured. Already-Confirmed / Dismissed / Split rows are NOT
/// reset by re-detection — once the analyst has decided, the row is
/// immutable on its lifecycle column.
/// </para>
/// </summary>
public sealed class CrossRecordScanService
{
    private readonly InspectionDbContext _db;
    private readonly IEnumerable<ICrossRecordScanDetector> _detectors;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly CaseWorkflowService _workflow;
    private readonly ILogger<CrossRecordScanService> _logger;

    public CrossRecordScanService(
        InspectionDbContext db,
        IEnumerable<ICrossRecordScanDetector> detectors,
        ITenantContext tenant,
        IEventPublisher events,
        CaseWorkflowService workflow,
        ILogger<CrossRecordScanService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _detectors = detectors ?? throw new ArgumentNullException(nameof(detectors));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Run every registered detector against the case + persist the
    /// outcomes. Returns the rows touched (created or updated).
    /// Best-effort: a single detector failing logs + skips, the rest
    /// continue.
    /// </summary>
    public async Task<IReadOnlyList<CrossRecordDetection>> ScanAndPersistAsync(
        Guid caseId,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "CrossRecordScanService cannot run without a resolved tenant context.");
        var tenantId = _tenant.TenantId;
        var now = DateTimeOffset.UtcNow;

        var touched = new List<CrossRecordDetection>();
        foreach (var detector in _detectors)
        {
            CrossRecordDetectionDescriptor? descriptor;
            try
            {
                descriptor = await detector.DetectAsync(caseId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Detector {Version} threw on case {CaseId}; skipping.",
                    detector.DetectorVersion, caseId);
                continue;
            }
            if (descriptor is null) continue;

            var existing = await _db.CrossRecordDetections.FirstOrDefaultAsync(
                d => d.CaseId == caseId && d.DetectorVersion == detector.DetectorVersion,
                ct);
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
                // Refresh the snapshot for an already-pending row so
                // the analyst sees the latest signals.
                existing.DetectedAt = now;
                existing.DetectedSubjectsJson = subjectsJson;
                existing.Notes = descriptor.Rationale;
            }
            // Confirmed / Dismissed / Split rows: leave alone.

            touched.Add(existing);
        }

        if (_db.ChangeTracker.HasChanges())
            await _db.SaveChangesAsync(ct);
        if (touched.Count > 0)
        {
            try
            {
                var payload = JsonSerializer.SerializeToElement(new
                {
                    caseId,
                    rowCount = touched.Count
                });
                var key = IdempotencyKey.ForEntityChange(
                    tenantId, "inspection.cross_record_detection.scanned",
                    "InspectionCase", caseId.ToString(), now);
                var evt = DomainEvent.Create(
                    tenantId, actorUserId: null,
                    correlationId: System.Diagnostics.Activity.Current?.RootId,
                    eventType: "inspection.cross_record_detection.scanned",
                    entityType: "InspectionCase",
                    entityId: caseId.ToString(),
                    payload: payload,
                    idempotencyKey: key);
                await _events.PublishAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to emit cross_record_detection.scanned for case {CaseId}.",
                    caseId);
            }
        }
        return touched;
    }

    /// <summary>
    /// Confirm a pending detection. The analyst agrees the case is
    /// multi-subject; the row stays in Confirmed state until they
    /// invoke <see cref="ExecuteSplitAsync"/>. Idempotent — confirming
    /// an already-Confirmed row refreshes the metadata without state
    /// change.
    /// </summary>
    public async Task<CrossRecordDetection> ConfirmAsync(
        Guid detectionId,
        Guid? actorUserId,
        string? notes,
        CancellationToken ct = default)
    {
        var row = await _db.CrossRecordDetections.FirstOrDefaultAsync(d => d.Id == detectionId, ct)
            ?? throw new InvalidOperationException($"Detection {detectionId} not found.");
        if (row.State == CrossRecordDetectionState.Split)
            throw new InvalidOperationException("Cannot confirm — case has already been split.");
        if (row.State == CrossRecordDetectionState.Dismissed)
            throw new InvalidOperationException("Cannot confirm — detection is dismissed.");

        var now = DateTimeOffset.UtcNow;
        row.State = CrossRecordDetectionState.Confirmed;
        row.ReviewedByUserId = actorUserId;
        row.ReviewedAt = now;
        if (!string.IsNullOrWhiteSpace(notes)) row.Notes = notes;
        await _db.SaveChangesAsync(ct);

        await EmitLifecycleEventAsync("inspection.cross_record_detection.confirmed", row, actorUserId, ct);
        return row;
    }

    /// <summary>
    /// Dismiss a pending or confirmed detection — the analyst
    /// considers it a false positive. The row sticks around for
    /// audit; future re-detections will skip it (the
    /// <see cref="CrossRecordDetectionState.Dismissed"/> state is a
    /// terminal latch).
    /// </summary>
    public async Task<CrossRecordDetection> DismissAsync(
        Guid detectionId,
        Guid? actorUserId,
        string? notes,
        CancellationToken ct = default)
    {
        var row = await _db.CrossRecordDetections.FirstOrDefaultAsync(d => d.Id == detectionId, ct)
            ?? throw new InvalidOperationException($"Detection {detectionId} not found.");
        if (row.State == CrossRecordDetectionState.Split)
            throw new InvalidOperationException("Cannot dismiss — case has already been split.");

        var now = DateTimeOffset.UtcNow;
        row.State = CrossRecordDetectionState.Dismissed;
        row.ReviewedByUserId = actorUserId;
        row.ReviewedAt = now;
        if (!string.IsNullOrWhiteSpace(notes)) row.Notes = notes;
        await _db.SaveChangesAsync(ct);

        await EmitLifecycleEventAsync("inspection.cross_record_detection.dismissed", row, actorUserId, ct);
        return row;
    }

    /// <summary>
    /// Execute the split. Calls
    /// <see cref="CaseWorkflowService.SplitCaseAsync"/> to create
    /// child cases for every detected subject (other than the
    /// parent's own); writes the resulting child ids back into
    /// <see cref="CrossRecordDetection.SplitCaseIdsJson"/> and flips
    /// the row to <see cref="CrossRecordDetectionState.Split"/>.
    /// </summary>
    public async Task<CrossRecordDetection> ExecuteSplitAsync(
        Guid detectionId,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var row = await _db.CrossRecordDetections.FirstOrDefaultAsync(d => d.Id == detectionId, ct)
            ?? throw new InvalidOperationException($"Detection {detectionId} not found.");
        if (row.State == CrossRecordDetectionState.Split)
            return row; // already done; idempotent
        if (row.State == CrossRecordDetectionState.Dismissed)
            throw new InvalidOperationException("Cannot split — detection is dismissed.");

        // Decode the subjects json. Skip the parent's own identifier.
        var subjects = DeserializeSubjects(row.DetectedSubjectsJson);
        if (subjects.Count == 0)
            throw new InvalidOperationException(
                $"Detection {detectionId} carries no subjects — cannot split.");

        var childIds = await _workflow.SplitCaseAsync(
            row.CaseId,
            subjects.Select(s => s.SubjectIdentifier).ToList(),
            ct);

        var now = DateTimeOffset.UtcNow;
        row.State = CrossRecordDetectionState.Split;
        row.SplitCaseIdsJson = JsonSerializer.Serialize(childIds);
        row.ReviewedByUserId = actorUserId;
        row.ReviewedAt = now;
        await _db.SaveChangesAsync(ct);

        await EmitLifecycleEventAsync("inspection.cross_record_detection.split", row, actorUserId, ct);
        return row;
    }

    /// <summary>
    /// List detections for the admin queue, optionally filtered by
    /// state. Newest first. Bounded by <paramref name="take"/> — the
    /// admin page uses 100 by default.
    /// </summary>
    public async Task<IReadOnlyList<CrossRecordDetectionRow>> ListAsync(
        CrossRecordDetectionState? state = null,
        int take = 100,
        CancellationToken ct = default)
    {
        if (take <= 0) take = 100;
        var query = _db.CrossRecordDetections.AsNoTracking();
        if (state is not null) query = query.Where(d => d.State == state.Value);
        var rows = await query
            .OrderByDescending(d => d.DetectedAt)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(r => new CrossRecordDetectionRow(
            Id: r.Id,
            CaseId: r.CaseId,
            DetectedAt: r.DetectedAt,
            DetectorVersion: r.DetectorVersion,
            State: r.State,
            Subjects: DeserializeSubjects(r.DetectedSubjectsJson),
            SplitCaseIds: DeserializeSplitIds(r.SplitCaseIdsJson),
            Notes: r.Notes,
            ReviewedAt: r.ReviewedAt,
            ReviewedByUserId: r.ReviewedByUserId)).ToList();
    }

    /// <summary>Get one detection row, decoded for the detail view.</summary>
    public async Task<CrossRecordDetectionRow?> GetAsync(Guid detectionId, CancellationToken ct = default)
    {
        var r = await _db.CrossRecordDetections.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == detectionId, ct);
        if (r is null) return null;
        return new CrossRecordDetectionRow(
            Id: r.Id,
            CaseId: r.CaseId,
            DetectedAt: r.DetectedAt,
            DetectorVersion: r.DetectorVersion,
            State: r.State,
            Subjects: DeserializeSubjects(r.DetectedSubjectsJson),
            SplitCaseIds: DeserializeSplitIds(r.SplitCaseIdsJson),
            Notes: r.Notes,
            ReviewedAt: r.ReviewedAt,
            ReviewedByUserId: r.ReviewedByUserId);
    }

    private async Task EmitLifecycleEventAsync(
        string eventType,
        CrossRecordDetection row,
        Guid? actorUserId,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                detectionId = row.Id,
                caseId = row.CaseId,
                state = row.State.ToString(),
                splitCaseIdsJson = row.SplitCaseIdsJson
            });
            var key = IdempotencyKey.ForEntityChange(
                row.TenantId, eventType,
                "CrossRecordDetection", row.Id.ToString(),
                row.ReviewedAt ?? DateTimeOffset.UtcNow);
            var evt = DomainEvent.Create(
                row.TenantId, actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: eventType,
                entityType: "CrossRecordDetection",
                entityId: row.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit {EventType} for detection {DetectionId}.",
                eventType, row.Id);
        }
    }

    private static IReadOnlyList<CrossRecordSubject> DeserializeSubjects(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return Array.Empty<CrossRecordSubject>();
        try
        {
            return JsonSerializer.Deserialize<List<CrossRecordSubject>>(json) ?? new List<CrossRecordSubject>();
        }
        catch (JsonException)
        {
            return Array.Empty<CrossRecordSubject>();
        }
    }

    private static IReadOnlyList<Guid> DeserializeSplitIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<Guid>();
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? new List<Guid>();
        }
        catch (JsonException)
        {
            return Array.Empty<Guid>();
        }
    }
}

/// <summary>
/// Sprint 31 / B5.2 — denormalised row for the admin queue page +
/// detail dialog. Decodes <see cref="CrossRecordDetection.DetectedSubjectsJson"/>
/// + <see cref="CrossRecordDetection.SplitCaseIdsJson"/> so the Razor
/// view doesn't have to JSON-parse on every render.
/// </summary>
public sealed record CrossRecordDetectionRow(
    Guid Id,
    Guid CaseId,
    DateTimeOffset DetectedAt,
    string DetectorVersion,
    CrossRecordDetectionState State,
    IReadOnlyList<CrossRecordSubject> Subjects,
    IReadOnlyList<Guid> SplitCaseIds,
    string? Notes,
    DateTimeOffset? ReviewedAt,
    Guid? ReviewedByUserId);
