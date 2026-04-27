# Team DP — Demo Path

## Mission

Close the case-lifecycle loop end-to-end. Today the FS6000 adapter's `StreamAsync` is dead code — only manual button clicks (`SimulateScanAsync`) consume it. Build a `ScannerIngestionWorker : BackgroundService` that runs `StreamAsync` per registered scanner and auto-creates cases on file emission. Refactor `SimulateScanAsync` to share an `IngestArtifactAsync(deviceId, raw, ct)` helper. Auto-fire `EvaluateAuthorityRulesAsync` after `FetchDocumentsAsync` succeeds.

After this team merges, dropping a real FS6000 triplet into the configured watch folder is the entire demo. No human button clicks until the analyst sets the verdict.

## Why this matters

The user's vision said "online-first; analysts get value end-to-end." Today the value chain has a manual click at the worst possible point — the moment when a real scan would naturally land. Until this gap closes, every demo requires explanation; after it closes, the demo demonstrates itself.

## Current state

- `modules/inspection/plugins/NickERP.Inspection.Scanners.FS6000/FS6000ScannerAdapter.cs` implements `StreamAsync` (polls `WatchPath`, emits one `RawScanArtifact` per completed triplet, dedup via in-process `_seen`). **Nothing calls it.**
- `modules/inspection/src/NickERP.Inspection.Web/Services/CaseWorkflowService.cs:SimulateScanAsync` (~lines 102–159):
  1. Resolves the adapter via `IPluginRegistry.Resolve<IScannerAdapter>(typeCode, ...)`.
  2. Calls `await foreach (var item in adapter.StreamAsync(config, ct)) { raw = item; break; }` — takes exactly one artifact then bails.
  3. Calls `adapter.ParseAsync(raw, ct)` to get a `ParsedArtifact`.
  4. Stashes parsed bytes via `_imageStore.SaveSourceAsync(...)`.
  5. Inserts `Scan` + `ScanArtifact` rows.
  6. Emits `nickerp.inspection.scan_recorded`.
- `FetchDocumentsAsync` (~lines 164–217) ends with the workflow advance to `Validated`. **No call to `EvaluateAuthorityRulesAsync`.** The CaseDetail page has a separate "Run authority checks" button.

## Deliverables

### 1. Refactor — extract `IngestArtifactAsync`

In `CaseWorkflowService.cs`, extract steps 3–6 of `SimulateScanAsync` into a private helper:

```csharp
private async Task<Scan> IngestArtifactAsync(
    Guid caseId,
    ScannerDeviceInstance device,
    IScannerAdapter adapter,
    RawScanArtifact raw,
    long tenantId,
    Guid? operatorUserId,
    CancellationToken ct)
{
    var parsed = await adapter.ParseAsync(raw, ct);
    var contentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(parsed.Bytes));
    var ext = MimeToExtension(parsed.MimeType);
    var storageUri = await _imageStore.SaveSourceAsync(contentHash, ext, parsed.Bytes, ct);

    var scan = new Scan
    {
        CaseId = caseId,
        ScannerDeviceInstanceId = device.Id,
        Mode = "ingested",
        CapturedAt = raw.CapturedAt,
        OperatorUserId = operatorUserId,
        IdempotencyKey = $"scan/{device.Id}/{contentHash[..16]}",
        CorrelationId = System.Diagnostics.Activity.Current?.RootId,
        TenantId = tenantId
    };
    _db.Scans.Add(scan);

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
        CreatedAt = DateTimeOffset.UtcNow,
        TenantId = tenantId
    };
    _db.ScanArtifacts.Add(artifact);
    await _db.SaveChangesAsync(ct);

    await EmitAsync(tenantId, operatorUserId, scan.CorrelationId,
        "nickerp.inspection.scan_recorded", "Scan",
        scan.Id.ToString(), new { scan.Id, scan.CaseId, scan.ScannerDeviceInstanceId }, ct);

    return scan;
}
```

Note the **idempotency key change** — the old version used `Guid.NewGuid()` so re-runs duplicated; the new key is content-addressed so re-ingesting the same scan triplet is a silent dedupe (assuming the unique index in deliverable 4).

`SimulateScanAsync` now becomes:

```csharp
public async Task<Scan> SimulateScanAsync(Guid caseId, Guid scannerDeviceInstanceId, CancellationToken ct = default)
{
    var (actor, tenant) = await CurrentActorAsync();
    var tenantId = EnsureTenant(tenant);

    var device = await _db.ScannerDeviceInstances.AsNoTracking()
        .FirstOrDefaultAsync(d => d.Id == scannerDeviceInstanceId, ct)
        ?? throw new InvalidOperationException($"Scanner device {scannerDeviceInstanceId} not found.");

    var adapter = _plugins.Resolve<IScannerAdapter>(device.TypeCode, _services);
    var config = new ScannerDeviceConfig(device.Id, device.LocationId, device.StationId, tenantId, device.ConfigJson);

    RawScanArtifact? raw = null;
    await foreach (var item in adapter.StreamAsync(config, ct))
    {
        raw = item;
        break;
    }
    if (raw is null) throw new InvalidOperationException("Adapter produced no artifact.");

    return await IngestArtifactAsync(caseId, device, adapter, raw, tenantId, actor, ct);
}
```

### 2. New: `ScannerIngestionWorker`

New file: `modules/inspection/src/NickERP.Inspection.Web/Services/ScannerIngestionWorker.cs`.

```csharp
using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Background service that drives every registered <see cref="ScannerDeviceInstance"/>
/// through its <see cref="IScannerAdapter.StreamAsync"/>. Each emitted
/// <see cref="RawScanArtifact"/> creates (or reuses) a case and ingests the
/// scan via <see cref="CaseWorkflowService.IngestRawArtifactAsync"/>.
///
/// Idempotency is content-addressed: the scan row's IdempotencyKey hashes the
/// source bytes, so re-ingesting the same triplet is a silent no-op (caught
/// at SaveChanges by a unique index on Scan.IdempotencyKey).
///
/// Per-instance loops run as separate Tasks; one slow scanner doesn't
/// block the others. Loops terminate on CancellationToken.
/// </summary>
public sealed class ScannerIngestionWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ScannerIngestionWorker> _logger;

    public ScannerIngestionWorker(IServiceProvider services, ILogger<ScannerIngestionWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Discover instances + spawn one streaming task each. Re-discover every
        // 60s so newly added instances pick up without a host restart.
        var perInstanceTasks = new Dictionary<Guid, Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
                var instances = await db.ScannerDeviceInstances.AsNoTracking()
                    .Where(d => d.IsActive)
                    .ToListAsync(stoppingToken);

                foreach (var instance in instances)
                {
                    if (perInstanceTasks.ContainsKey(instance.Id)) continue;
                    perInstanceTasks[instance.Id] = Task.Run(
                        () => StreamForInstanceAsync(instance.Id, stoppingToken), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ScannerIngestionWorker discovery failed; retry in 60s");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task StreamForInstanceAsync(Guid instanceId, CancellationToken ct)
    {
        // One scope per instance — DbContext is scoped, can live the loop.
        // Outermost try is "log + back off + restart" so a transient adapter
        // exception doesn't kill the loop forever.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var workflow = scope.ServiceProvider.GetRequiredService<CaseWorkflowService>();
                var db = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
                var plugins = scope.ServiceProvider.GetRequiredService<IPluginRegistry>();
                var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

                var instance = await db.ScannerDeviceInstances.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == instanceId && d.IsActive, ct);
                if (instance is null) return;

                // Worker runs without an HTTP request, so no user/tenant context
                // is in scope. Inject the instance's tenant manually.
                tenant.SetTenant(instance.TenantId);

                var adapter = plugins.Resolve<IScannerAdapter>(instance.TypeCode, scope.ServiceProvider);
                var config = new ScannerDeviceConfig(
                    instance.Id, instance.LocationId, instance.StationId,
                    instance.TenantId, instance.ConfigJson);

                _logger.LogInformation("ScannerIngestionWorker streaming for instance {Id} ({TypeCode})",
                    instance.Id, instance.TypeCode);

                await foreach (var raw in adapter.StreamAsync(config, ct))
                {
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        await workflow.IngestRawArtifactAsync(instance, adapter, raw, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to ingest artifact {Path} from instance {Id}",
                            raw.SourcePath, instance.Id);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ScannerIngestionWorker loop crashed for {Id}; restart in 30s", instanceId);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (TaskCanceledException) { return; }
            }
        }
    }
}
```

### 3. New `CaseWorkflowService.IngestRawArtifactAsync` public method

The worker needs a top-level entry that handles **case lookup or creation** plus the existing `IngestArtifactAsync` ingest. Add to `CaseWorkflowService`:

```csharp
/// <summary>
/// Top-level entry for hosted-service-driven ingestion. Looks up or creates
/// the case keyed by (LocationId, SubjectIdentifier=stem); then runs the
/// shared ingest helper.
/// </summary>
public async Task<Scan> IngestRawArtifactAsync(
    ScannerDeviceInstance instance,
    IScannerAdapter adapter,
    RawScanArtifact raw,
    CancellationToken ct = default)
{
    var tenantId = instance.TenantId;
    var stem = ExtractSubjectIdentifier(raw.SourcePath);

    // Find or create the case. Subject identifier = filename stem; if a case
    // for the same identifier+location is already open within the last 24h,
    // reuse it; otherwise open a fresh one.
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
    if (existing is not null) { @case = existing; }
    else
    {
        @case = await OpenCaseAsync(
            instance.LocationId,
            CaseSubjectType.Container, // TODO: Per-instance subject type config; default Container.
            stem,
            instance.StationId,
            ct);
    }

    return await IngestArtifactAsync(@case.Id, instance, adapter, raw, tenantId, operatorUserId: null, ct);
}

private static string ExtractSubjectIdentifier(string sourcePath)
{
    // For FS6000, SourcePath is the file stem (no high.img/low.img/material.img suffix).
    // Take the leaf folder + filename stem; if the path is inscrutable, fall back to a hash.
    var name = Path.GetFileName(sourcePath);
    return string.IsNullOrEmpty(name) ? sourcePath.GetHashCode().ToString("X") : name;
}
```

### 4. Migration: unique index on `Scan.IdempotencyKey`

Add a migration that creates a unique index `ux_scans_idempotency_key` on `inspection.scans("IdempotencyKey")`. With content-addressed keys this guarantees re-ingesting the same triplet doesn't duplicate.

**Caveat:** this clashes with the existing `Guid.NewGuid()` keys in the table (test data). The migration must include a pre-step `DELETE FROM inspection.scans WHERE "Mode"='synthetic';` OR use a non-unique index for now and fix in a follow-up. **Recommend: non-unique index for safety**, with a TODO to make it unique once the legacy synthetic rows are gone. Document the choice in the commit message.

### 5. Auto-fire authority rules after `FetchDocumentsAsync`

In `CaseWorkflowService.FetchDocumentsAsync`, at the end (after the `case_validated` event emit and before the return), add:

```csharp
// Auto-evaluate authority rules so the analyst sees violations on first
// case-detail render rather than having to click "Run authority checks".
// Best-effort — a misbehaving rule pack must not break document fetch.
try
{
    await EvaluateAuthorityRulesAsync(caseId, ct);
}
catch (Exception ex)
{
    _logger.LogWarning(ex, "Auto-evaluate of authority rules failed after fetch for case {CaseId}", caseId);
}
```

In `modules/inspection/src/NickERP.Inspection.Web/Components/Pages/CaseDetail.razor`, after `FetchDocuments` returns, also reload `_rulesResult` from a new method that **reads** rather than re-evaluates (or just calls `EvaluateAuthorityRulesAsync` again if cheap enough — for first cut, the latter is fine and matches existing behavior). The "Run authority checks" button stays for analyst-driven re-runs.

### 6. Register the worker

In `modules/inspection/src/NickERP.Inspection.Web/Program.cs`, after the existing service registrations (and after Team PM's `AddNickErpImaging` → `AddHostedService<PreRenderWorker>` if PM landed first):

```csharp
builder.Services.AddHostedService<NickERP.Inspection.Web.Services.ScannerIngestionWorker>();
```

## Acceptance criteria

1. **Build green** — `dotnet build` returns 0 errors.

2. **Existing flow unbroken** — manual case open + `Simulate scan` button on `/cases/{id}` still works (proves the refactor preserved behavior).

3. **Auto-ingest works:**
   - Configure one Tema location, one FS6000 instance with `WatchPath: C:\test\fs6000-incoming\` and `PollIntervalSeconds: 5`.
   - Drop a real FS6000 triplet (or use Team TF's fixture: `tests/TestData/fs6000/sample-100x100.{high,low,material}.img` — copy them as `0001high.img`, `0001low.img`, `0001material.img` into the watch folder).
   - Within 30 seconds: `/cases` shows a new case with subject identifier `"0001"`. The case has one scan + one ScanArtifact + thumbnail rendered.
   - Within 5 more seconds (PreRender poll), thumbnail loads on `/cases`.

4. **Auto-rules works:** open the new case, click "Fetch documents" against an `icums-gh` instance with a sample batch in its drop folder. After return: the authority-checks panel is populated with any violations from `gh-customs`. (No second click needed.)

5. **Idempotent re-drop:** copy the same triplet again as `0001high.img` etc. (or `0002high.img` matching the same content). Within 30 seconds: no new case appears (same subject identifier, within 24h reuse window, content-addressed key dedupes). Verify via Postgres count.

6. **Multi-scanner:** add a second FS6000 instance pointed at a different watch folder; both run concurrently; instance crash doesn't kill the other.

7. **Audit trail:** Portal `/audit` shows `case_opened`, `scan_recorded`, `document_fetched` (×N), `case_validated`, `rules_evaluated` events from the auto-flow.

## Out of scope

- Don't add scheduled `ExternalSystemAdapter.FetchDocumentsAsync` polling — that's a future iteration. For now the analyst clicks Fetch.
- Don't add a queue / lease for the `ScannerIngestionWorker` — running multiple host instances would race; document this as a single-host-only constraint until the SQL-backed durable queue lands (`§7.7`).
- Don't add tests — Team TF.

## Dependencies

- **Inbound:**
  - **Team PT** must merge first (or together) — the worker constructs `ScannerDeviceConfig` with `TenantId`.
  - **Team TS** must merge first or together — the `tenant.SetTenant(instance.TenantId)` call in the worker depends on `ITenantContext.SetTenant` being callable from a non-HTTP scope, which TS preserves (TS removed the auto-default-to-1; the worker explicitly sets it).
- **Outbound:** Team TF will write the end-to-end smoke test against this; Team PM's eviction janitor needs to know what scan_artifacts look like in this auto-flow.

## Notes / gotchas

- **Stem-as-subject-identifier is a placeholder** for the demo. Real production may want the `ContainerNumber` parsed from the filename or a metadata file. Add a TODO; not in this sprint.
- **`tenant.SetTenant` from a worker.** This is a thread-local-style context; the worker is in its own DI scope so this is fine. If `ITenantContext` is `IAsyncLocal`-backed, also fine. If it's request-scoped only, you'll need an alternate path — read the impl in `platform/NickERP.Platform.Tenancy/`.
- **Auto-rule evaluation is per-case, on document fetch.** It's not on every page load; that would be wasteful. The CaseDetail page reads any persisted RuleEvaluation rows (which will exist after V3 lands next sprint). For now, the panel is in-memory state per page render.

## Commit message convention

```
feat(inspection): scanner ingestion worker — demo path closed (Sprint DP)

Inspection v2 v0.1's end-to-end happy path now runs without manual clicks:

  - Refactored CaseWorkflowService.SimulateScanAsync; extracted shared
    IngestArtifactAsync helper (parse → save source → insert scan +
    artifact → emit scan_recorded).
  - Content-addressed Scan.IdempotencyKey (sha256 of source bytes,
    first 16 chars) — re-ingesting the same triplet is a no-op.
  - New ScannerIngestionWorker BackgroundService: enumerates active
    ScannerDeviceInstance rows every 60s, runs IScannerAdapter.StreamAsync
    per instance in independent tasks, calls
    CaseWorkflowService.IngestRawArtifactAsync on emission. Looks up
    or creates the case keyed by (LocationId, stem, opened-within-24h).
  - FetchDocumentsAsync now auto-fires EvaluateAuthorityRulesAsync at
    the end (best-effort, doesn't block fetch on rule errors).
  - Non-unique index on inspection.scans.IdempotencyKey (TODO: tighten
    to UNIQUE once legacy synthetic rows are cleaned).

Verified end-to-end: drop a real FS6000 triplet into WatchPath →
within 30s a case appears on /cases with thumbnail; click in, click
Fetch documents → authority panel populates without a second click.
Re-dropping the same triplet creates no duplicate.

Co-Authored-By: Claude (Sprint Team DP)
```
