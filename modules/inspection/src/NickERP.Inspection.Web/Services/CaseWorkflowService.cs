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
using NickERP.Platform.Telemetry;
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
        // Sprint E1 — `ServerAuthenticationStateProvider.GetAuthenticationStateAsync`
        // throws InvalidOperationException ("Do not call ... outside of the
        // DI scope for a Razor component") when invoked from a hosted-service
        // scope (ScannerIngestionWorker), because there's no Blazor circuit
        // backing it. Catch + treat as anonymous; the worker's IngestRawArtifactAsync
        // explicitly opens cases with OpenedByUserId=null anyway, and the
        // tenant id below comes from ITenantContext (which the worker sets
        // before calling in). HTTP-driven Razor calls keep their normal
        // behavior — the cascading auth state still resolves the principal
        // for them.
        Guid? id = null;
        try
        {
            var state = await _auth.GetAuthenticationStateAsync();
            var idClaim = state.User?.FindFirst("nickerp:id")?.Value;
            if (Guid.TryParse(idClaim, out var g)) id = g;
        }
        catch (InvalidOperationException)
        {
            // Outside a Razor scope (worker / background job). The actor
            // is anonymous; tenant comes from ITenantContext below.
            id = null;
        }
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
        // Sprint A2 — opening a case is a state transition from "none" to Open.
        NickErpActivity.CaseStateTransitions.Add(1,
            new KeyValuePair<string, object?>("from", "none"),
            new KeyValuePair<string, object?>("to", InspectionWorkflowState.Open.ToString()));

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
    // "synthetic"); D2's worker passes "ingested" so the audit log can
    // distinguish auto-ingest from a button click.
    //
    // D2: Scan.IdempotencyKey is content-addressed — sha-256 prefix of the
    // parsed source bytes — so re-ingesting the same triplet (e.g. after
    // a worker restart that revisits an already-emitted file) collides on
    // the existing (TenantId, IdempotencyKey) unique index and is treated
    // as a silent no-op rather than producing a duplicate scan row.
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
        // Sprint A2 — wall-clock the entire ingest helper so the /perf
        // page can show ingestion-throughput p95s, scoped per scanner
        // type. try/finally guarantees a record on every return path
        // (idempotency short-circuit + benign-race recover paths
        // included). Record only on success-y returns; throws bubble
        // through unrecorded since they don't represent valid latency.
        var ingestSw = System.Diagnostics.Stopwatch.StartNew();
        var scannerTypeTag = string.IsNullOrEmpty(device.TypeCode) ? "unknown" : device.TypeCode;
        try
        {
        var now = DateTimeOffset.UtcNow;
        var parsed = await adapter.ParseAsync(raw, ct);

        // Hash the parsed bytes first — the same hash is used for the
        // content-addressed Scan.IdempotencyKey, the on-disk storage
        // filename via SaveSourceAsync, and the ScanArtifact.ContentHash.
        var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(parsed.Bytes));

        // D2 — re-ingest of the same triplet (within a tenant) collapses
        // to a single Scan row. The unique index `ux_scans_tenant_idempotency`
        // on (TenantId, IdempotencyKey) catches duplicates at SaveChanges;
        // we look up the existing row and return it instead of throwing.
        var idempotencyKey = $"scan/{device.Id}/{contentHash[..16]}";
        var existingScan = await _db.Scans
            .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, ct);
        if (existingScan is not null)
        {
            _logger.LogDebug(
                "Scan with IdempotencyKey {Key} already exists (Scan {Id}); skipping re-ingest.",
                idempotencyKey, existingScan.Id);
            return existingScan;
        }

        var scan = new Scan
        {
            CaseId = caseId,
            ScannerDeviceInstanceId = device.Id,
            Mode = mode,
            CapturedAt = now,
            OperatorUserId = operatorUserId,
            IdempotencyKey = idempotencyKey,
            CorrelationId = System.Diagnostics.Activity.Current?.RootId,
            TenantId = tenantId
        };
        _db.Scans.Add(scan);

        // Stash the adapter's parsed bytes into the content-addressed image
        // store so the pre-render worker (and re-render after configuration
        // change later) can reach back for them. StorageUri points to the
        // disk location instead of the adapter's transient SourcePath.
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
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsScanIdempotencyKeyViolation(ex))
        {
            // A concurrent worker beat us to it. Re-read the existing row
            // and return that — same content => same scan, no duplicate.
            _logger.LogDebug(ex,
                "Lost benign race inserting Scan with IdempotencyKey {Key}; returning existing row.",
                idempotencyKey);
            _db.Entry(scan).State = EntityState.Detached;
            _db.Entry(artifact).State = EntityState.Detached;
            var winner = await _db.Scans.AsNoTracking()
                .FirstOrDefaultAsync(s => s.IdempotencyKey == idempotencyKey, ct)
                ?? throw new InvalidOperationException(
                    "Idempotency-key collision reported but no existing scan row found.");
            return winner;
        }

        await EmitAsync(tenantId, operatorUserId, scan.CorrelationId, "nickerp.inspection.scan_recorded", "Scan",
            scan.Id.ToString(), new { scan.Id, scan.CaseId, scan.ScannerDeviceInstanceId }, ct);

        return scan;
        }
        finally
        {
            ingestSw.Stop();
            NickErpActivity.ScanIngestMs.Record(
                ingestSw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("scanner_type_code", scannerTypeTag));
        }
    }

    private static bool IsScanIdempotencyKeyViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name.Contains("PostgresException", StringComparison.Ordinal) == true
           && ex.InnerException.Message.Contains("ux_scans_tenant_idempotency", StringComparison.Ordinal);

    // ---------------------------------------------------------------------
    // D2 — Top-level entry for hosted-service-driven ingestion. Looks up
    // or creates the case keyed by (LocationId, SubjectIdentifier=stem)
    // within a 24h reuse window, then runs the shared private
    // IngestArtifactAsync helper.
    //
    // The worker is process-wide with no HTTP request context, so
    // _tenant.IsResolved may be false; we must SetTenant explicitly from
    // the device instance's TenantId before any DB write or the F1
    // throw-on-unresolved interceptor will reject the insert.
    // ---------------------------------------------------------------------
    public async Task<Scan> IngestRawArtifactAsync(
        ScannerDeviceInstance instance,
        IScannerAdapter adapter,
        RawScanArtifact raw,
        CancellationToken ct = default)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (adapter is null) throw new ArgumentNullException(nameof(adapter));
        if (raw is null) throw new ArgumentNullException(nameof(raw));

        // The worker scope already calls _tenant.SetTenant(instance.TenantId),
        // but be defensive — IngestRawArtifactAsync should be safe to call
        // from any non-HTTP context (tests, future schedulers) without the
        // caller having to know the tenancy contract.
        if (!_tenant.IsResolved || _tenant.TenantId != instance.TenantId)
            _tenant.SetTenant(instance.TenantId);

        var tenantId = EnsureTenant(instance.TenantId);
        var stem = ExtractSubjectIdentifier(raw.SourcePath);

        // Reuse an open case with the same (LocationId, SubjectIdentifier)
        // opened in the last 24h. Anything older — or already terminal —
        // gets a fresh case so we don't accidentally fold a re-scan of a
        // long-since-closed container into the previous workflow run.
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var existing = await _db.Cases
            .Where(c => c.LocationId == instance.LocationId
                        && c.SubjectIdentifier == stem
                        && c.OpenedAt > since
                        && c.State != InspectionWorkflowState.Closed
                        && c.State != InspectionWorkflowState.Cancelled)
            .OrderByDescending(c => c.OpenedAt)
            .FirstOrDefaultAsync(ct);

        InspectionCase @case;
        if (existing is not null)
        {
            @case = existing;
        }
        else
        {
            // OpenCaseAsync calls CurrentActorAsync which would normally
            // pull the user id from the auth claims. In the worker path
            // there's no authenticated principal, so the case lands with
            // OpenedByUserId=null. The tenant context is already set
            // above, so the F1 throw-on-unresolved guard is satisfied.
            @case = await OpenCaseAsync(
                instance.LocationId,
                CaseSubjectType.Container, // TODO: per-instance subject-type config; default Container.
                stem,
                instance.StationId,
                ct);
        }

        return await IngestArtifactAsync(
            @case.Id,
            instance,
            adapter,
            raw,
            tenantId,
            operatorUserId: null,
            mode: "ingested",
            ct);
    }

    /// <summary>
    /// Derive a stable subject identifier from a <see cref="RawScanArtifact.SourcePath"/>.
    /// For FS6000 the SourcePath is the file stem (no high.img/low.img/material.img
    /// suffix); falling back to a hash of the path keeps non-empty rows even
    /// for adapters that emit something unexpected.
    /// </summary>
    private static string ExtractSubjectIdentifier(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        return string.IsNullOrEmpty(name)
            ? sourcePath.GetHashCode().ToString("X")
            : name;
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
        // Sprint A2 — capture the prior state BEFORE mutation so the
        // state_transitions counter can tag the actual from→to pair
        // (Validated emission is gated below; we only count when the
        // emit fires).
        var priorStateForValidated = c.State;
        bool transitionedToValidated = false;
        if (c.State == InspectionWorkflowState.Open && emitted.Count > 0)
        {
            c.State = InspectionWorkflowState.Validated;
            c.StateEnteredAt = now;
            transitionedToValidated = true;
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
            if (transitionedToValidated)
            {
                NickErpActivity.CaseStateTransitions.Add(1,
                    new KeyValuePair<string, object?>("from", priorStateForValidated.ToString()),
                    new KeyValuePair<string, object?>("to", InspectionWorkflowState.Validated.ToString()));
            }
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

        // Sprint A1 — persist a per-authority snapshot so the rules pane
        // survives page reload. One row per (CaseId, AuthorityCode);
        // re-evaluation overwrites the existing row (snapshot semantics).
        // Provider errors get attached to the authority that owns them
        // when the message is prefixed with the type code (matches the
        // "{TypeCode}: ..." format used above); orphan errors land on a
        // synthetic row keyed by the empty AuthorityCode so the analyst
        // can still see them after reload.
        await PersistRuleEvaluationsAsync(c, tenantId, allViolations, allMutations, providerErrors, registered, ct);

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

    // ---------------------------------------------------------------------
    // Sprint A1 — snapshot writer.
    //
    // Groups violations + mutations + provider-errors by AuthorityCode, then
    // upserts one row per (CaseId, AuthorityCode) into
    // <c>inspection.rule_evaluations</c>. Re-running rules for the same case
    // overwrites the previous snapshot — historical evaluations live on the
    // audit stream via <c>nickerp.inspection.rules_evaluated</c>, not in
    // this table.
    //
    // We collect the union of authority codes from (a) successfully-resolved
    // providers, (b) violations, (c) mutations, and (d) errors so an
    // authority that returned 0/0/0 still gets a "we ran, nothing to flag"
    // snapshot row — that's what the page-reload hydration relies on to
    // show "No violations" instead of an empty pane.
    // ---------------------------------------------------------------------
    private async Task PersistRuleEvaluationsAsync(
        InspectionCase @case,
        long tenantId,
        IReadOnlyList<EvaluatedViolation> violations,
        IReadOnlyList<EvaluatedMutation> mutations,
        IReadOnlyList<string> providerErrors,
        IReadOnlyList<RegisteredPlugin> registeredProviders,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Map TypeCode → AuthorityCode for any provider we successfully
        // resolved during the loop above. Used to attribute "{TypeCode}:
        // ..." prefixed error strings back to the right authority row.
        var typeCodeToAuthority = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in registeredProviders)
        {
            try
            {
                var resolved = _plugins.Resolve<IAuthorityRulesProvider>(p.TypeCode, _services);
                typeCodeToAuthority[p.TypeCode] = resolved.AuthorityCode;
            }
            catch
            {
                // Resolve failure already recorded as a provider error
                // upstream; we just skip it for the type-code map.
            }
        }

        string? AuthorityForError(string err)
        {
            // Provider error format: "{TypeCode}: ..." — see above.
            var idx = err.IndexOf(':');
            if (idx <= 0) return null;
            var typeCode = err[..idx];
            return typeCodeToAuthority.TryGetValue(typeCode, out var authority) ? authority : null;
        }

        // Bucket by AuthorityCode. An empty-string key holds errors we
        // couldn't attribute (e.g. the registry itself threw before we got
        // a TypeCode mapping). Successful providers with zero findings
        // still get a bucket so reload renders "clean" cases correctly.
        var buckets = new Dictionary<string, (List<EvaluatedViolation> Violations, List<EvaluatedMutation> Mutations, List<string> Errors)>(StringComparer.OrdinalIgnoreCase);

        (List<EvaluatedViolation>, List<EvaluatedMutation>, List<string>) BucketFor(string authority)
        {
            if (!buckets.TryGetValue(authority, out var b))
            {
                b = (new List<EvaluatedViolation>(), new List<EvaluatedMutation>(), new List<string>());
                buckets[authority] = b;
            }
            return b;
        }

        foreach (var authority in typeCodeToAuthority.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            BucketFor(authority);

        foreach (var v in violations) BucketFor(v.AuthorityCode).Item1.Add(v);
        foreach (var m in mutations) BucketFor(m.AuthorityCode).Item2.Add(m);
        foreach (var err in providerErrors)
        {
            var authority = AuthorityForError(err) ?? string.Empty;
            BucketFor(authority).Item3.Add(err);
        }

        if (buckets.Count == 0) return;

        // Upsert: load existing rows for this case, update in place if
        // matched, otherwise insert. The unique index on
        // (TenantId, CaseId, AuthorityCode) backs the snapshot semantic.
        var caseId = @case.Id;
        var existing = await _db.RuleEvaluations
            .Where(r => r.CaseId == caseId)
            .ToListAsync(ct);
        var existingByAuthority = existing.ToDictionary(
            r => r.AuthorityCode,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (authority, bucket) in buckets)
        {
            var violationsJson = JsonSerializer.Serialize(bucket.Violations);
            var mutationsJson = JsonSerializer.Serialize(bucket.Mutations);
            var errorsJson = JsonSerializer.Serialize(bucket.Errors);

            if (existingByAuthority.TryGetValue(authority, out var row))
            {
                row.EvaluatedAt = now;
                row.ViolationsJson = violationsJson;
                row.MutationsJson = mutationsJson;
                row.ProviderErrorsJson = errorsJson;
            }
            else
            {
                _db.RuleEvaluations.Add(new RuleEvaluation
                {
                    Id = Guid.NewGuid(),
                    CaseId = caseId,
                    AuthorityCode = authority,
                    EvaluatedAt = now,
                    ViolationsJson = violationsJson,
                    MutationsJson = mutationsJson,
                    ProviderErrorsJson = errorsJson,
                    TenantId = tenantId
                });
            }
        }

        await _db.SaveChangesAsync(ct);
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
        // Sprint A2 — capture pre-mutation state for the transition counter.
        var priorState = c.State;
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
        NickErpActivity.CaseStateTransitions.Add(1,
            new KeyValuePair<string, object?>("from", priorState.ToString()),
            new KeyValuePair<string, object?>("to", InspectionWorkflowState.Assigned.ToString()));
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
            // AssignSelfAndStartReviewAsync mutates c.State to Assigned and
            // emits its own transition counter. Re-fetch the case so we
            // see the post-assign state for the verdict transition tag below.
            session = await AssignSelfAndStartReviewAsync(caseId, ct);
            c = await _db.Cases.FirstOrDefaultAsync(x => x.Id == caseId, ct)
                ?? throw new InvalidOperationException($"Case {caseId} not found after auto-assign.");
        }
        // Sprint A2 — capture pre-Verdict state for the transition counter.
        var priorState = c.State;

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
        NickErpActivity.CaseStateTransitions.Add(1,
            new KeyValuePair<string, object?>("from", priorState.ToString()),
            new KeyValuePair<string, object?>("to", InspectionWorkflowState.Verdict.ToString()));
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

        // Sprint A2 — capture pre-Submit state so the transition counter
        // can tag the actual from→to (we only count when SubmitAsync
        // actually moves the case forward — i.e. on Accepted).
        var priorState = c.State;
        bool transitioned = false;
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
                transitioned = true;
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
        if (transitioned)
        {
            NickErpActivity.CaseStateTransitions.Add(1,
                new KeyValuePair<string, object?>("from", priorState.ToString()),
                new KeyValuePair<string, object?>("to", InspectionWorkflowState.Submitted.ToString()));
        }
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
        // Sprint A2 — capture pre-mutation state for the transition counter.
        var priorState = c.State;
        c.State = InspectionWorkflowState.Closed;
        c.StateEnteredAt = now;
        c.ClosedAt = now;
        await _db.SaveChangesAsync(ct);

        await EmitAsync(tenantId, actor, c.CorrelationId, "nickerp.inspection.case_closed", "InspectionCase",
            c.Id.ToString(), new { c.Id }, ct);
        NickErpActivity.CaseStateTransitions.Add(1,
            new KeyValuePair<string, object?>("from", priorState.ToString()),
            new KeyValuePair<string, object?>("to", InspectionWorkflowState.Closed.ToString()));
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
