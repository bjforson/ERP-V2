using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Authorities.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Inspection.Imaging;
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
    private readonly IImageStore _imageStore;
    private readonly ILogger<CaseWorkflowService> _logger;

    public CaseWorkflowService(
        InspectionDbContext db,
        IEventPublisher events,
        IPluginRegistry plugins,
        IServiceProvider services,
        ITenantContext tenant,
        AuthenticationStateProvider auth,
        IImageStore imageStore,
        ILogger<CaseWorkflowService> logger)
    {
        _db = db;
        _events = events;
        _plugins = plugins;
        _services = services;
        _tenant = tenant;
        _auth = auth;
        _imageStore = imageStore;
        _logger = logger;
    }

    private async Task<(Guid? UserId, long TenantId)> CurrentActorAsync()
    {
        var state = await _auth.GetAuthenticationStateAsync();
        var idClaim = state.User?.FindFirst("nickerp:id")?.Value;
        Guid? id = Guid.TryParse(idClaim, out var g) ? g : null;
        // Phase F1 — fail loud instead of silently coercing to tenant 1. If
        // we get here without a resolved tenant, the request bypassed the
        // UseNickErpTenancy() middleware (e.g. an endpoint forgot to require
        // auth) and any write would land cross-tenant. RLS would also reject
        // the SELECT/INSERT now that policies are in place.
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved. Verify NickErpTenancy middleware ran for this request "
                + "(it must follow UseAuthentication/UseAuthorization in Program.cs) and that the "
                + "principal carries a valid 'nickerp:tenant_id' claim.");
        }
        return (id, _tenant.TenantId);
    }

    // Phase F1 — EnsureTenant previously coerced 0 → 1 as a fallback. With
    // CurrentActorAsync now throwing on unresolved tenants, every caller
    // already has a positive TenantId; the helper is kept as an identity-pass
    // so call sites stay readable but no longer hides tenancy bugs.
    private static long EnsureTenant(long t) =>
        t > 0
            ? t
            : throw new InvalidOperationException(
                $"EnsureTenant received a non-positive tenant id ({t}). This indicates a bug in tenant resolution.");

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

        var device = await _db.ScannerDeviceInstances.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == scannerDeviceInstanceId, ct)
            ?? throw new InvalidOperationException($"Scanner device {scannerDeviceInstanceId} not found.");

        // Resolve the adapter plugin and stream one synthetic artifact.
        var adapter = _plugins.Resolve<IScannerAdapter>(device.TypeCode, _services);
        var config = new ScannerDeviceConfig(device.Id, device.LocationId, device.StationId, tenantId, device.ConfigJson);

        RawScanArtifact? raw = null;
        await foreach (var item in adapter.StreamAsync(config, ct))
        {
            raw = item;
            break;
        }
        if (raw is null) throw new InvalidOperationException("Adapter produced no artifact.");

        return await IngestArtifactAsync(
            caseId,
            device,
            adapter,
            raw,
            tenantId,
            operatorUserId: actor,
            mode: "synthetic",
            ct);
    }

    // ---------------------------------------------------------------------
    // Shared ingest helper — parse the adapter output, stash bytes in the
    // content-addressed image store, insert Scan + ScanArtifact, and emit
    // nickerp.inspection.scan_recorded. Both the operator-driven
    // SimulateScanAsync button path and the (D2) ScannerIngestionWorker
    // call this; keeping the side-effects in one place ensures the audit
    // trail and DB writes are identical regardless of trigger.
    //
    // D1 invariant: this is a pure refactor of the previous in-line block
    // in SimulateScanAsync. Same Scan/ScanArtifact rows, same
    // SaveSourceAsync call (same content hash + extension), same
    // DomainEvent. Caller now picks the Mode string (operator path stays
    // "synthetic"); D2's worker will pass "ingested" so the audit log can
    // distinguish auto-ingest from a button click.
    //
    // TODO(D2): switch Scan.IdempotencyKey from random-Guid to a
    // content-addressed sha256-prefix key (e.g. $"scan/{device.Id}/{contentHash[..16]}")
    // so re-ingesting the same triplet on worker restart is a silent no-op.
    // The change ships with D2's dedup-on-unique-index migration; staying
    // with the random key here keeps D1 a behaviour-preserving refactor.
    // ---------------------------------------------------------------------
    private async Task<Scan> IngestArtifactAsync(
        Guid caseId,
        ScannerDeviceInstance device,
        IScannerAdapter adapter,
        RawScanArtifact raw,
        long tenantId,
        Guid? operatorUserId,
        string mode,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var parsed = await adapter.ParseAsync(raw, ct);

        var scan = new Scan
        {
            CaseId = caseId,
            ScannerDeviceInstanceId = device.Id,
            Mode = mode,
            CapturedAt = now,
            OperatorUserId = operatorUserId,
            IdempotencyKey = $"scan/{device.Id}/{Guid.NewGuid():N}",
            CorrelationId = System.Diagnostics.Activity.Current?.RootId,
            TenantId = tenantId
        };
        _db.Scans.Add(scan);

        // Stash the adapter's parsed bytes into the content-addressed image
        // store so the pre-render worker (and re-render after configuration
        // change later) can reach back for them. StorageUri points to the
        // disk location instead of the adapter's transient SourcePath.
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(parsed.Bytes));
        var ext = MimeToExtension(parsed.MimeType);
        var storageUri = await _imageStore.SaveSourceAsync(contentHash, ext, parsed.Bytes, ct);

        var artifact = new ScanArtifact
        {
            ScanId = scan.Id,
            ArtifactKind = "Primary",
            StorageUri = storageUri,
            MimeType = parsed.MimeType,
            WidthPx = parsed.WidthPx,
            HeightPx = parsed.HeightPx,
            Channels = parsed.Channels,
            ContentHash = contentHash,
            MetadataJson = JsonSerializer.Serialize(parsed.Metadata),
            CreatedAt = now,
            TenantId = tenantId
        };
        _db.ScanArtifacts.Add(artifact);
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, operatorUserId, scan.CorrelationId, "nickerp.inspection.scan_recorded", "Scan",
            scan.Id.ToString(), new { scan.Id, scan.CaseId, scan.ScannerDeviceInstanceId }, ct);

        return scan;
    }

    // ---------------------------------------------------------------------
    // Fetch authority documents via an IExternalSystemAdapter plugin
    //
    // D3 — after persisting the new documents and emitting the
    // case_validated event, automatically run the authority rules pack
    // so the analyst doesn't need a second click. Rule evaluation is
    // best-effort: a throwing provider is logged and the rules result
    // comes back as null, but the document fetch itself still succeeds
    // (the analyst can re-run via the "Run authority checks" button).
    // ---------------------------------------------------------------------
    public async Task<FetchDocumentsResult> FetchDocumentsAsync(Guid caseId, Guid externalSystemInstanceId, CancellationToken ct = default)
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
            new ExternalSystemConfig(instance.Id, tenantId, instance.ConfigJson),
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

        // D3 auto-fire: run the authority-rules pack so the analyst sees
        // violations / suggested mutations as soon as the documents land.
        // Wrapped so a throwing provider doesn't undo the fetch — the
        // documents are already saved and the case is already Validated.
        RulesEvaluationResult? rules = null;
        try
        {
            rules = await EvaluateAuthorityRulesAsync(caseId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Auto-evaluating authority rules failed after document fetch for case {CaseId}; analyst can re-run manually.",
                caseId);
        }

        return new FetchDocumentsResult(emitted, rules);
    }

    // ---------------------------------------------------------------------
    // Evaluate authority rules — Validate + Infer over every registered
    // IAuthorityRulesProvider plugin. Read-only; surfaces violations and
    // suggested mutations to the UI but does not mutate the case.
    //
    // Multiple providers may be registered (one per authority — Ghana
    // Customs, Nigeria Customs, etc.). For now we run all of them; future
    // versions could scope by case Location → authority mapping.
    // ---------------------------------------------------------------------
    public async Task<RulesEvaluationResult> EvaluateAuthorityRulesAsync(
        Guid caseId,
        CancellationToken ct = default)
    {
        var (actor, tenant) = await CurrentActorAsync();
        var tenantId = EnsureTenant(tenant);

        var c = await _db.Cases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");

        var docs = await _db.AuthorityDocuments.AsNoTracking()
            .Where(d => d.CaseId == caseId)
            .OrderBy(d => d.ReceivedAt)
            .ToListAsync(ct);

        var scans = await _db.Scans.AsNoTracking()
            .Where(s => s.CaseId == caseId)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);

        // Build the LocationCode lookup for the case's location — rules
        // need it (port-match in particular) and the entity carries Code
        // as its stable identifier.
        var location = await _db.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == c.LocationId, ct);
        var locationCode = location?.Code ?? string.Empty;

        // Load scanner instance metadata so we can populate ScanSnapshot —
        // each Scan carries a ScannerDeviceInstanceId; the type code lives
        // on the instance row.
        var deviceIds = scans.Select(s => s.ScannerDeviceInstanceId).Distinct().ToList();
        var devicesById = await _db.ScannerDeviceInstances.AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, ct);

        // Aggregate scan-level metadata from the artifacts. The FS6000
        // adapter (and future adapters) puts useful keys on each artifact's
        // MetadataJson — flatten them into the snapshot so rules can read
        // e.g. "scanner.fyco_present" without a second query.
        var scanIds = scans.Select(s => s.Id).ToList();
        var artifacts = await _db.ScanArtifacts.AsNoTracking()
            .Where(a => scanIds.Contains(a.ScanId))
            .ToListAsync(ct);
        var artifactsByScan = artifacts
            .GroupBy(a => a.ScanId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var scanSnapshots = scans.Select(s =>
        {
            devicesById.TryGetValue(s.ScannerDeviceInstanceId, out var dev);
            var typeCode = dev?.TypeCode ?? string.Empty;
            var meta = MergeArtifactMetadata(artifactsByScan.GetValueOrDefault(s.Id));
            return new ScanSnapshot(
                ScannerTypeCode: typeCode,
                LocationCode: locationCode,
                Mode: s.Mode,
                CapturedAt: s.CapturedAt,
                Metadata: meta);
        }).ToList();

        var docSnapshots = docs.Select(d => new AuthorityDocumentSnapshot(
            DocumentType: d.DocumentType,
            ReferenceNumber: d.ReferenceNumber,
            PayloadJson: d.PayloadJson)).ToList();

        var caseData = new InspectionCaseData(
            CaseId: c.Id,
            TenantId: c.TenantId,
            SubjectType: c.SubjectType.ToString(),
            SubjectIdentifier: c.SubjectIdentifier,
            Documents: docSnapshots,
            Scans: scanSnapshots);

        // Run every registered IAuthorityRulesProvider. If a provider
        // throws we collect its error rather than failing the whole pass —
        // a misbehaving rule pack shouldn't block the analyst's ability
        // to see the others.
        var allViolations = new List<EvaluatedViolation>();
        var allMutations = new List<EvaluatedMutation>();
        var providerErrors = new List<string>();
        var registered = _plugins.ForContract(typeof(IAuthorityRulesProvider));
        foreach (var p in registered)
        {
            IAuthorityRulesProvider provider;
            try
            {
                provider = _plugins.Resolve<IAuthorityRulesProvider>(p.TypeCode, _services);
            }
            catch (Exception ex)
            {
                providerErrors.Add($"{p.TypeCode}: resolve failed — {ex.Message}");
                continue;
            }

            try
            {
                var validation = await provider.ValidateAsync(caseData, ct);
                foreach (var v in validation.Violations)
                    allViolations.Add(new EvaluatedViolation(provider.AuthorityCode, v));

                var inference = await provider.InferAsync(caseData, ct);
                foreach (var m in inference.Mutations)
                    allMutations.Add(new EvaluatedMutation(provider.AuthorityCode, m));
            }
            catch (Exception ex)
            {
                providerErrors.Add($"{p.TypeCode}: {ex.Message}");
                _logger.LogWarning(ex, "Rules provider {TypeCode} threw during case {CaseId} evaluation",
                    p.TypeCode, caseId);
            }
        }

        await EmitAsync(tenantId, actor, c.CorrelationId,
            "nickerp.inspection.rules_evaluated", "InspectionCase", c.Id.ToString(),
            new
            {
                c.Id,
                providersRun = registered.Count,
                violationCount = allViolations.Count,
                mutationCount = allMutations.Count,
                errorCount = providerErrors.Count
            }, ct);

        return new RulesEvaluationResult(allViolations, allMutations, providerErrors);
    }

    /// <summary>
    /// Best-effort MIME → file extension. The image store needs an extension
    /// for the on-disk filename so external tools can identify the format
    /// without sniffing.
    /// </summary>
    private static string MimeToExtension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/tiff" => ".tiff",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        _ => ".bin"
    };

    /// <summary>
    /// Flatten every artifact's <c>MetadataJson</c> into a single dictionary.
    /// Later artifacts win on key conflict — most-recent wins, which matches
    /// how the rules consume scan facts (latest scan is the most relevant).
    /// </summary>
    private static IReadOnlyDictionary<string, string> MergeArtifactMetadata(IReadOnlyList<ScanArtifact>? artifacts)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        if (artifacts is null || artifacts.Count == 0) return merged;
        foreach (var a in artifacts)
        {
            if (string.IsNullOrEmpty(a.MetadataJson)) continue;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(a.MetadataJson);
                if (dict is null) continue;
                foreach (var kv in dict) merged[kv.Key] = kv.Value;
            }
            catch (JsonException) { /* skip malformed metadata */ }
        }
        return merged;
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
                new ExternalSystemConfig(instance.Id, tenantId, instance.ConfigJson),
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

/// <summary>Combined output of every IAuthorityRulesProvider run against a case.</summary>
public sealed record RulesEvaluationResult(
    IReadOnlyList<EvaluatedViolation> Violations,
    IReadOnlyList<EvaluatedMutation> Mutations,
    IReadOnlyList<string> ProviderErrors);

/// <summary>
/// What <see cref="CaseWorkflowService.FetchDocumentsAsync"/> hands back:
/// the persisted documents plus the optional auto-fired rules pack output.
/// <see cref="Rules"/> is <c>null</c> when the auto-evaluation threw — the
/// fetch itself still succeeded; the analyst can re-run via the "Run
/// authority checks" button. The non-null shape exists so callers can
/// surface the rules pane on first render without a second round-trip.
/// </summary>
public sealed record FetchDocumentsResult(
    IReadOnlyList<NickERP.Inspection.Core.Entities.AuthorityDocument> Documents,
    RulesEvaluationResult? Rules);

/// <summary>One rule violation, tagged with the authority that produced it.</summary>
public sealed record EvaluatedViolation(string AuthorityCode, RuleViolation Violation);

/// <summary>One inferred mutation, tagged with the authority that produced it.</summary>
public sealed record EvaluatedMutation(string AuthorityCode, InferredMutation Mutation);
