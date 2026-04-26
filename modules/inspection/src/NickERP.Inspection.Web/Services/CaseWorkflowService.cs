using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Encapsulates every case-lifecycle state transition + the DomainEvent
/// emission that goes with it. Pages call this; pages don't write to the
/// DbContext directly for workflow operations. Keeps the audit log
/// consistent and the workflow invariants in one place.
/// </summary>
public sealed class CaseWorkflowService
{
    private readonly InspectionDbContext _db;
    private readonly IEventPublisher _events;
    private readonly IPluginRegistry _plugins;
    private readonly IServiceProvider _services;
    private readonly ITenantContext _tenant;
    private readonly AuthenticationStateProvider _auth;
    private readonly ILogger<CaseWorkflowService> _logger;

    public CaseWorkflowService(
        InspectionDbContext db,
        IEventPublisher events,
        IPluginRegistry plugins,
        IServiceProvider services,
        ITenantContext tenant,
        AuthenticationStateProvider auth,
        ILogger<CaseWorkflowService> logger)
    {
        _db = db;
        _events = events;
        _plugins = plugins;
        _services = services;
        _tenant = tenant;
        _auth = auth;
        _logger = logger;
    }

    private async Task<(Guid? UserId, long TenantId)> CurrentActorAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var idClaim = state.User?.FindFirst("nickerp:id")?.Value;
        Guid? id = Guid.TryParse(idClaim, out var g) ? g : null;
        if (!_tenant.IsResolved) _tenant.SetTenant(1);
        return (id, _tenant.TenantId);
    }

    private static long EnsureTenant(long t) => t > 0 ? t : 1;

    // ---------------------------------------------------------------------
    // Open a new case
    // ---------------------------------------------------------------------
    public async Task<InspectionCase> OpenCaseAsync(
        Guid locationId,
        CaseSubjectType subjectType,
        string subjectIdentifier,
        Guid? stationId,
        CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);
        var now = DateTimeOffset.UtcNow;

        var c = new InspectionCase
        {
            LocationId = locationId,
            StationId = stationId,
            SubjectType = subjectType,
            SubjectIdentifier = subjectIdentifier.Trim(),
            SubjectPayloadJson = "{}",
            State = InspectionWorkflowState.Open,
            OpenedAt = now,
            StateEnteredAt = now,
            OpenedByUserId = actor,
            CorrelationId = System.Diagnostics.Activity.Current?.RootId,
            TenantId = tenantId
        };
        _db.Cases.Add(c);
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.case_opened", "InspectionCase",
            c.Id.ToString(), new { c.Id, c.LocationId, c.SubjectType, c.SubjectIdentifier }, ct);

        return c;
    }

    // ---------------------------------------------------------------------
    // Simulate a scan via a registered IScannerAdapter plugin
    // ---------------------------------------------------------------------
    public async Task<Scan> SimulateScanAsync(Guid caseId, Guid scannerDeviceInstanceId, CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);
        var now = DateTimeOffset.UtcNow;

        var device = await _db.ScannerDeviceInstances.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == scannerDeviceInstanceId, ct)
            ?? throw new InvalidOperationException($"Scanner device {scannerDeviceInstanceId} not found.");

        // Resolve the adapter plugin and stream one synthetic artifact.
        var adapter = _plugins.Resolve<IScannerAdapter>(device.TypeCode, _services);
        var config = new ScannerDeviceConfig(device.Id, device.LocationId, device.StationId, device.ConfigJson);

        RawScanArtifact? raw = null;
        await foreach (var item in adapter.StreamAsync(config, ct))
        {
            raw = item;
            break;
        }
        if (raw is null) throw new InvalidOperationException("Adapter produced no artifact.");
        var parsed = await adapter.ParseAsync(raw, ct);

        var scan = new Scan
        {
            CaseId = caseId,
            ScannerDeviceInstanceId = device.Id,
            Mode = "synthetic",
            CapturedAt = now,
            OperatorUserId = actor,
            IdempotencyKey = $"scan/{device.Id}/{Guid.NewGuid():N}",
            CorrelationId = System.Diagnostics.Activity.Current?.RootId,
            TenantId = tenantId
        };
        _db.Scans.Add(scan);

        var artifact = new ScanArtifact
        {
            ScanId = scan.Id,
            ArtifactKind = "Primary",
            StorageUri = raw.SourcePath,
            MimeType = parsed.MimeType,
            WidthPx = parsed.WidthPx,
            HeightPx = parsed.HeightPx,
            Channels = parsed.Channels,
            ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(parsed.Bytes)),
            MetadataJson = JsonSerializer.Serialize(parsed.Metadata),
            CreatedAt = now,
            TenantId = tenantId
        };
        _db.ScanArtifacts.Add(artifact);
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, scan.CorrelationId, "nickerp.inspection.scan_recorded", "Scan",
            scan.Id.ToString(), new { scan.Id, scan.CaseId, scan.ScannerDeviceInstanceId }, ct);

        return scan;
    }

    // ---------------------------------------------------------------------
    // Fetch authority documents via an IExternalSystemAdapter plugin
    // ---------------------------------------------------------------------
    public async Task<IReadOnlyList<NickERP.Inspection.Core.Entities.AuthorityDocument>> FetchDocumentsAsync(Guid caseId, Guid externalSystemInstanceId, CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);
        var now = DateTimeOffset.UtcNow;

        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");
        var instance = await _db.ExternalSystemInstances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == externalSystemInstanceId, ct)
            ?? throw new InvalidOperationException($"ExternalSystemInstance {externalSystemInstanceId} not found.");

        var adapter = _plugins.Resolve<IExternalSystemAdapter>(instance.TypeCode, _services);
        var docs = await adapter.FetchDocumentsAsync(
            new ExternalSystemConfig(instance.Id, instance.ConfigJson),
            new CaseLookupCriteria(c.SubjectIdentifier, null, null),
            ct);

        var emitted = new List<NickERP.Inspection.Core.Entities.AuthorityDocument>();
        foreach (var d in docs)
        {
            var row = new NickERP.Inspection.Core.Entities.AuthorityDocument
            {
                CaseId = caseId,
                ExternalSystemInstanceId = instance.Id,
                DocumentType = d.DocumentType,
                ReferenceNumber = d.ReferenceNumber,
                PayloadJson = d.PayloadJson,
                ReceivedAt = d.ReceivedAt,
                TenantId = tenantId
            };
            _db.AuthorityDocuments.Add(row);
            emitted.Add(row);
        }

        // Move workflow forward: Open → Validated.
        if (c.State == InspectionWorkflowState.Open && emitted.Count > 0)
        {
            c.State = InspectionWorkflowState.Validated;
            c.StateEnteredAt = now;
        }
        await _db.SaveChangesAsync(ct);

        foreach (var row in emitted)
        {
            await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.document_fetched", "AuthorityDocument",
                row.Id.ToString(), new { row.Id, row.CaseId, row.DocumentType, row.ReferenceNumber }, ct);
        }
        if (c.State == InspectionWorkflowState.Validated)
        {
            await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.case_validated", "InspectionCase",
                c.Id.ToString(), new { c.Id, c.State }, ct);
        }
        return emitted;
    }

    // ---------------------------------------------------------------------
    // Assign current user to a case + start a review session
    // ---------------------------------------------------------------------
    public async Task<ReviewSession> AssignSelfAndStartReviewAsync(Guid caseId, CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);
        if (actor is null) throw new InvalidOperationException("Cannot assign — no authenticated user.");
        var now = DateTimeOffset.UtcNow;

        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");
        c.AssignedAnalystUserId = actor;
        c.State = InspectionWorkflowState.Assigned;
        c.StateEnteredAt = now;

        var session = new ReviewSession
        {
            CaseId = c.Id,
            AnalystUserId = actor.Value,
            StartedAt = now,
            Outcome = "in-progress",
            TenantId = tenantId
        };
        _db.ReviewSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.case_assigned", "InspectionCase",
            c.Id.ToString(), new { c.Id, AnalystUserId = actor }, ct);
        return session;
    }

    // ---------------------------------------------------------------------
    // Set the verdict (creates AnalystReview + Verdict, advances state)
    // ---------------------------------------------------------------------
    public async Task<Verdict> SetVerdictAsync(
        Guid caseId,
        VerdictDecision decision,
        string basis,
        double confidence,
        CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);
        if (actor is null) throw new InvalidOperationException("Cannot set verdict — no authenticated user.");
        var now = DateTimeOffset.UtcNow;

        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");

        var session = await _db.ReviewSessions
            .Where(s => s.CaseId == caseId && s.AnalystUserId == actor.Value && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (session is null)
        {
            session = await AssignSelfAndStartReviewAsync(caseId, ct);
        }

        var review = new AnalystReview
        {
            ReviewSessionId = session.Id,
            TimeToDecisionMs = (int)Math.Min(int.MaxValue, (now - session.StartedAt).TotalMilliseconds),
            ConfidenceScore = Math.Clamp(confidence, 0.0, 1.0),
            CreatedAt = now,
            TenantId = tenantId
        };
        _db.AnalystReviews.Add(review);

        session.EndedAt = now;
        session.Outcome = "completed";

        var verdict = new Verdict
        {
            CaseId = caseId,
            Decision = decision,
            Basis = basis,
            DecidedAt = now,
            DecidedByUserId = actor.Value,
            TenantId = tenantId
        };
        _db.Verdicts.Add(verdict);

        c.State = InspectionWorkflowState.Verdict;
        c.StateEnteredAt = now;

        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.verdict_set", "Verdict",
            verdict.Id.ToString(), new { verdict.Id, verdict.CaseId, verdict.Decision, verdict.Basis, review.ConfidenceScore }, ct);
        return verdict;
    }

    // ---------------------------------------------------------------------
    // Submit the verdict to an external system
    // ---------------------------------------------------------------------
    public async Task<OutboundSubmission> SubmitAsync(Guid caseId, Guid externalSystemInstanceId, CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);
        var now = DateTimeOffset.UtcNow;

        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");
        var v = await _db.Verdicts.FirstOrDefaultAsync(x => x.CaseId == caseId, ct)
            ?? throw new InvalidOperationException("Cannot submit — no verdict on this case yet.");
        var instance = await _db.ExternalSystemInstances.AsNoTracking().FirstOrDefaultAsync(x => x.Id == externalSystemInstanceId, ct)
            ?? throw new InvalidOperationException($"ExternalSystemInstance {externalSystemInstanceId} not found.");

        var idempotencyKey = IdempotencyKey.From(tenantId, "submission", caseId, v.Id, instance.Id);
        var payload = JsonSerializer.Serialize(new { caseId, decision = v.Decision.ToString(), basis = v.Basis });

        var sub = new OutboundSubmission
        {
            CaseId = caseId,
            ExternalSystemInstanceId = instance.Id,
            PayloadJson = payload,
            IdempotencyKey = idempotencyKey,
            Status = "pending",
            SubmittedAt = now,
            TenantId = tenantId
        };
        _db.OutboundSubmissions.Add(sub);
        await _db.SaveChangesAsync(ct);

        try
        {
            var adapter = _plugins.Resolve<IExternalSystemAdapter>(instance.TypeCode, _services);
            var result = await adapter.SubmitAsync(
                new ExternalSystemConfig(instance.Id, instance.ConfigJson),
                new OutboundSubmissionRequest(idempotencyKey, c.SubjectIdentifier, payload),
                ct);

            sub.Status = result.Accepted ? "accepted" : "rejected";
            sub.ResponseJson = result.AuthorityResponseJson;
            sub.ErrorMessage = result.Error;
            sub.RespondedAt = DateTimeOffset.UtcNow;

            if (result.Accepted)
            {
                c.State = InspectionWorkflowState.Submitted;
                c.StateEnteredAt = sub.RespondedAt.Value;
            }
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            sub.Status = "error";
            sub.ErrorMessage = ex.Message;
            sub.RespondedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "Outbound submission failed for case {CaseId}", caseId);
        }

        await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.submission_dispatched", "OutboundSubmission",
            sub.Id.ToString(), new { sub.Id, sub.CaseId, sub.Status }, ct);
        return sub;
    }

    // ---------------------------------------------------------------------
    // Close the case
    // ---------------------------------------------------------------------
    public async Task CloseCaseAsync(Guid caseId, CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);
        var now = DateTimeOffset.UtcNow;

        var c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");
        c.State = InspectionWorkflowState.Closed;
        c.StateEnteredAt = now;
        c.ClosedAt = now;
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.case_closed", "InspectionCase",
            c.Id.ToString(), new { c.Id }, ct);
    }

    // ---------------------------------------------------------------------
    private async Task EmitAsync(
        long tenantId, Guid? actor, string? correlationId,
        string eventType, string entityType, string entityId, object payload,
        CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(tenantId, eventType, entityType, entityId, DateTimeOffset.UtcNow);
            var evt = DomainEvent.Create(tenantId, actor, correlationId, eventType, entityType, entityId, json, key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            // Audit emission must not break user-facing workflows.
            _logger.LogWarning(ex, "Failed to emit DomainEvent {EventType} for {EntityType} {EntityId}", eventType, entityType, entityId);
        }
    }
}
