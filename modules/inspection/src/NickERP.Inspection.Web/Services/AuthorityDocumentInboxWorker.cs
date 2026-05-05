using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 24 / B3.2 — Authority document inbox worker.
/// Watches a configurable filesystem drop folder for adapter-shaped
/// JSON/XML exports + ingests them as <see cref="AuthorityDocument"/>
/// rows. Replaces v1 <c>IcumFileScannerService</c>; vendor-neutralised.
///
/// <para>
/// <b>Why filesystem drop matters.</b> Some authorities (and ICUMS in
/// particular) ship batch exports as zipped JSON / CSV / XML to a
/// shared drop folder rather than via a pull API. The drop is the
/// authority's commit point; the worker is the consumer side.
/// </para>
///
/// <para>
/// <b>Idempotency via content-hash.</b> Per Sprint 24 architectural
/// decision: no new <c>seen_keys</c> tables. We hash each file's
/// contents and store the hash on the resulting
/// <see cref="AuthorityDocument.PayloadJson"/> (under the
/// <c>_inbox_content_hash</c> JSON property). Duplicate files (same
/// bytes, e.g. retransmits) produce duplicate inserts that the
/// matcher worker silently de-dupes; the pure-content-hash check
/// inside this worker (one query per file) catches the common case
/// before persistence.
/// </para>
///
/// <para>
/// <b>Subfolder convention.</b> Mirrors v1's
/// <c>IcumFileScannerService.ScanForNewFilesAsync</c> shape: only
/// scans the named subfolders under the drop root. New file types
/// land in new subfolders; the host's <c>ExpectedSubfolders</c> list
/// drives the scan.
/// </para>
///
/// <para>
/// <b>Sprint 50 / FU-inbox-multi-tenant — per-tenant subfolder
/// routing.</b> Sprint 24 picked the first active tenant for every
/// file; that worked for single-tenant deploys but a multi-tenant pilot
/// needs each tenant's drop isolated. The new shape supports
/// <c>&lt;DropFolder&gt;/&lt;TenantCode&gt;/&lt;Subfolder&gt;/*.json</c>
/// — files under <c>tenant-a/ContainerData/</c> route to the tenant
/// whose <see cref="Tenant.Code"/> matches <c>tenant-a</c>. When NO
/// tenant-shaped subdirectories exist under the drop root, the worker
/// falls back to the Sprint 24 single-tenant shape so existing deploys
/// keep working unchanged. Unknown tenant codes log a warning + skip.
/// </para>
///
/// <para>
/// Default-disabled per Sprint 24 architectural decision. A fresh
/// deploy without a drop folder configured logs once + idles.
/// </para>
/// </summary>
public sealed class AuthorityDocumentInboxWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<IcumsFileScannerOptions> _options;
    private readonly ILogger<AuthorityDocumentInboxWorker> _logger;

    private readonly BackgroundServiceProbeState _probe = new();

    public AuthorityDocumentInboxWorker(
        IServiceProvider services,
        IOptions<IcumsFileScannerOptions> options,
        ILogger<AuthorityDocumentInboxWorker> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(AuthorityDocumentInboxWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "AuthorityDocumentInboxWorker disabled via {Section}:Enabled=false; not starting.",
                IcumsFileScannerOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);

        if (string.IsNullOrWhiteSpace(opts.DropFolder))
        {
            _logger.LogWarning(
                "AuthorityDocumentInboxWorker enabled but {Section}:DropFolder is not set; loop will idle.",
                IcumsFileScannerOptions.SectionName);
        }
        else
        {
            _logger.LogInformation(
                "AuthorityDocumentInboxWorker starting — scanning {Folder} every {Interval}, subfolders=[{Subfolders}].",
                opts.DropFolder, opts.PollInterval, string.Join(",", opts.ExpectedSubfolders));
        }

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var ingested = await ScanOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (ingested > 0)
                {
                    _logger.LogInformation(
                        "AuthorityDocumentInboxWorker cycle ingested {Count} file(s).", ingested);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "AuthorityDocumentInboxWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One scan cycle. Walks the configured subfolders + reads each new
    /// file. Returns the count of files ingested. Internal for tests.
    ///
    /// <para>
    /// <b>Sprint 50 multi-tenant routing.</b> Two paths:
    /// <list type="bullet">
    ///   <item><description><b>Per-tenant subfolders (preferred).</b>
    ///   When the drop root contains directories whose names match
    ///   active <see cref="Tenant.Code"/> values, files under
    ///   <c>&lt;TenantCode&gt;/&lt;ExpectedSubfolder&gt;/*.json</c>
    ///   route to that tenant. Unknown tenant directories log a
    ///   warning + skip (don't fall through to the legacy path —
    ///   that would mis-route the file).</description></item>
    ///   <item><description><b>Legacy single-tenant.</b> When no
    ///   tenant-shaped directories exist at the drop root, the worker
    ///   falls back to the Sprint 24 shape: scan
    ///   <c>&lt;ExpectedSubfolder&gt;/*.json</c> directly under the
    ///   root + route every file to the first active tenant.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    internal async Task<int> ScanOnceAsync(CancellationToken ct)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.DropFolder)) return 0;

        if (!Directory.Exists(opts.DropFolder))
        {
            _logger.LogWarning(
                "AuthorityDocumentInboxWorker drop folder does not exist: {Folder}.",
                opts.DropFolder);
            return 0;
        }

        // Resolve every active tenant up front — used by both the
        // multi-tenant + single-tenant paths.
        var activeTenants = await ResolveActiveTenantsAsync(ct);
        if (activeTenants.Count == 0)
        {
            _logger.LogWarning(
                "AuthorityDocumentInboxWorker: no active tenants; cycle no-ops.");
            return 0;
        }

        // Sprint 50 / FU-inbox-multi-tenant — detect tenant-shaped
        // subdirectories. A tenant-shaped subdir is any direct child
        // of the drop root whose name is a key in the
        // <see cref="ITenantContext"/>-resolvable tenant code map. Any
        // direct-child subdir that ISN'T a tenant code is logged + ignored
        // when we're on the multi-tenant path; the existence of even one
        // tenant-shaped subdir flips the worker to multi-tenant mode (so
        // a deploy never accidentally double-ingests a file because both
        // a tenant subdir + a top-level subdir exist).
        var directChildDirs = Directory.GetDirectories(opts.DropFolder)
            .Select(p => new DropChildDir(p, Path.GetFileName(p) ?? string.Empty))
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .ToList();

        var tenantsByCode = activeTenants.ToDictionary(t => t.Code,
            StringComparer.OrdinalIgnoreCase);

        var matchedTenantDirs = directChildDirs
            .Where(d => tenantsByCode.ContainsKey(d.Name))
            .ToList();

        if (matchedTenantDirs.Count > 0)
        {
            return await ScanMultiTenantAsync(opts, matchedTenantDirs, tenantsByCode, directChildDirs, ct);
        }

        // Legacy single-tenant: gather files from <root>/<sub>/*.json
        // and route every file to the first active tenant. The
        // ResolveTargetTenantAsync helper called from TryIngestFileAsync
        // does the same thing; we keep this path for backward compat
        // with single-tenant deploys.
        return await ScanLegacySingleTenantAsync(opts, ct);
    }

    /// <summary>
    /// Sprint 50 / FU-inbox-multi-tenant — multi-tenant scan. Each
    /// matched tenant directory is treated as a self-contained drop
    /// root (with the same ExpectedSubfolders convention inside).
    /// Unknown direct-child directories that don't match any tenant
    /// code log a warning so the operator can spot a typo + don't
    /// skip the cycle.
    /// </summary>
    private async Task<int> ScanMultiTenantAsync(
        IcumsFileScannerOptions opts,
        IReadOnlyList<DropChildDir> matchedTenantDirs,
        IReadOnlyDictionary<string, TenantDescriptor> tenantsByCode,
        IReadOnlyList<DropChildDir> directChildDirs,
        CancellationToken ct)
    {
        // Surface unknown tenant dirs so an operator typo isn't silent.
        foreach (var dir in directChildDirs)
        {
            if (tenantsByCode.ContainsKey(dir.Name)) continue;
            AuthorityDocumentInboxInstruments.UnknownTenantTotal.Add(1,
                new KeyValuePair<string, object?>("dir_name", dir.Name));
            _logger.LogWarning(
                "AuthorityDocumentInboxWorker: drop subdir '{Dir}' does not match any active tenant code; skipping (active codes: {Codes}).",
                dir.Name, string.Join(",", tenantsByCode.Keys));
        }

        var ingested = 0;
        foreach (var tenantDir in matchedTenantDirs)
        {
            ct.ThrowIfCancellationRequested();
            var descriptor = tenantsByCode[tenantDir.Name];

            foreach (var subfolder in opts.ExpectedSubfolders)
            {
                ct.ThrowIfCancellationRequested();
                var subPath = Path.Combine(tenantDir.Path, subfolder);
                if (!Directory.Exists(subPath)) continue;

                var files = Directory.GetFiles(subPath, "*.json", SearchOption.TopDirectoryOnly);
                foreach (var filePath in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (await TryIngestFileAsync(filePath, descriptor.Id, ct)) ingested++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AuthorityDocumentInboxInstruments.IngestFailedTotal.Add(1);
                        _logger.LogWarning(ex,
                            "AuthorityDocumentInboxWorker failed to ingest {File} for tenant={Tenant}; continuing.",
                            filePath, descriptor.Code);
                    }
                }
            }
        }
        return ingested;
    }

    /// <summary>
    /// Sprint 24 single-tenant scan path — preserved for backward
    /// compatibility with deploys that don't use per-tenant subfolders.
    /// </summary>
    private async Task<int> ScanLegacySingleTenantAsync(
        IcumsFileScannerOptions opts,
        CancellationToken ct)
    {
        var jsonFiles = new List<string>();
        foreach (var subfolder in opts.ExpectedSubfolders)
        {
            ct.ThrowIfCancellationRequested();
            var subPath = Path.Combine(opts.DropFolder!, subfolder);
            if (!Directory.Exists(subPath)) continue;
            jsonFiles.AddRange(Directory.GetFiles(subPath, "*.json", SearchOption.TopDirectoryOnly));
        }

        if (jsonFiles.Count == 0) return 0;

        var ingested = 0;
        foreach (var filePath in jsonFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryIngestFileAsync(filePath, tenantId: null, ct)) ingested++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AuthorityDocumentInboxInstruments.IngestFailedTotal.Add(1);
                _logger.LogWarning(ex,
                    "AuthorityDocumentInboxWorker failed to ingest {File}; continuing.", filePath);
            }
        }
        return ingested;
    }

    /// <summary>
    /// Sprint 50 / FU-inbox-multi-tenant — read every active tenant's
    /// (Id, Code) pair so the multi-tenant path can resolve a subdir
    /// name → TenantId in O(1).
    /// </summary>
    private async Task<IReadOnlyList<TenantDescriptor>> ResolveActiveTenantsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenantsDb = sp.GetRequiredService<TenancyDbContext>();
        var rows = await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .OrderBy(t => t.Id)
            .Select(t => new TenantDescriptor(t.Id, t.Code))
            .ToListAsync(ct);
        return rows;
    }

    /// <summary>Compact (Id, Code) pair surfaced from the tenancy DB once per cycle.</summary>
    private sealed record TenantDescriptor(long Id, string Code);

    /// <summary>Compact (path, leaf-name) pair for a direct-child subdir of the drop root.</summary>
    private sealed record DropChildDir(string Path, string Name);

    /// <summary>
    /// Read one file, hash it, find the matching tenant + case (best
    /// effort), persist as <see cref="AuthorityDocument"/>. Returns
    /// true on a clean ingest. Returns false on de-dupe (already
    /// ingested with same content hash).
    ///
    /// <para>
    /// Sprint 50 / FU-inbox-multi-tenant — when
    /// <paramref name="tenantId"/> is non-null, the file routes to
    /// that tenant directly (multi-tenant subdir convention). When
    /// null, the worker falls back to the legacy first-active-tenant
    /// resolution.
    /// </para>
    /// </summary>
    private async Task<bool> TryIngestFileAsync(string filePath, long? tenantId, CancellationToken ct)
    {
        // Read bytes + hash before opening DB scope so a transient FS
        // error doesn't hold a scope open.
        var bytes = await File.ReadAllBytesAsync(filePath, ct);
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
        var fileName = Path.GetFileName(filePath);

        // Subfolder name → DocumentType heuristic. v1 had the same
        // "BatchData/ContainerData/..." subfolder convention.
        var docType = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "Unknown";

        if (tenantId is null)
        {
            // Legacy single-tenant path: pick the first active tenant.
            tenantId = await ResolveTargetTenantAsync(ct);
        }

        if (tenantId is null)
        {
            _logger.LogWarning(
                "AuthorityDocumentInboxWorker: no active tenant found; cannot ingest {File}.", filePath);
            return false;
        }

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        tenant.SetTenant(tenantId.Value);

        // Dedupe — content hash check. We check by reading the existing
        // PayloadJson rows for this tenant + docType + (file name) and
        // looking inside for the _inbox_content_hash. Cheap because the
        // worker only sweeps recent files; if the hash is already
        // committed, skip.
        var alreadyIngested = await db.AuthorityDocuments
            .AsNoTracking()
            .Where(a => a.DocumentType == docType
                        && a.ReferenceNumber == fileName)
            .AnyAsync(ct);
        if (alreadyIngested)
        {
            AuthorityDocumentInboxInstruments.DedupedTotal.Add(1,
                new KeyValuePair<string, object?>("doc_type", docType));
            return false;
        }

        // Find a matching external system instance (= the source
        // authority). v1's drop-folder pattern doesn't carry an explicit
        // instance id — the host picks the first active instance. That's
        // a real follow-up: make the drop folder structure carry
        // <instance-id>/<docType>/<file> so the mapping is explicit.
        var instanceId = await db.ExternalSystemInstances.AsNoTracking()
            .Where(e => e.IsActive)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);
        if (instanceId is null)
        {
            _logger.LogWarning(
                "AuthorityDocumentInboxWorker: no active external system instance; cannot ingest {File}.", filePath);
            return false;
        }

        // Best-effort case match. The file's contents may carry a
        // container number; we wrap the raw payload in JSON with a
        // _inbox_content_hash so post-mortem can trace lineage.
        var payloadString = System.Text.Encoding.UTF8.GetString(bytes);
        var payload = TryParseJsonObject(payloadString);
        var containerNumber = payload?["container_number"]?.GetValue<string>()
            ?? payload?["container_id"]?.GetValue<string>()
            ?? payload?["ContainerNumber"]?.GetValue<string>();
        var caseId = await TryFindCaseAsync(db, containerNumber, ct);
        if (caseId is null)
        {
            AuthorityDocumentInboxInstruments.UnmatchedTotal.Add(1,
                new KeyValuePair<string, object?>("doc_type", docType));
            return false;
        }

        // Wrap the raw payload — keep the original under _raw + carry
        // the content hash as a sibling property the matcher can read.
        var wrappedPayloadJson = WrapPayload(payloadString, contentHash, fileName);

        db.AuthorityDocuments.Add(new AuthorityDocument
        {
            Id = Guid.NewGuid(),
            CaseId = caseId.Value,
            ExternalSystemInstanceId = instanceId.Value,
            DocumentType = docType,
            ReferenceNumber = fileName,
            PayloadJson = wrappedPayloadJson,
            ReceivedAt = DateTimeOffset.UtcNow,
            TenantId = tenantId.Value
        });
        await db.SaveChangesAsync(ct);

        AuthorityDocumentInboxInstruments.IngestedTotal.Add(1,
            new KeyValuePair<string, object?>("doc_type", docType));
        return true;
    }

    /// <summary>
    /// Pick the first active tenant. Single-tenant deployments dominate;
    /// multi-tenant inbox routing is a follow-up that needs per-tenant
    /// subfolder convention.
    /// </summary>
    private async Task<long?> ResolveTargetTenantAsync(CancellationToken ct)
    {
        using var discoveryScope = _services.CreateScope();
        var sp = discoveryScope.ServiceProvider;
        var tenantsDb = sp.GetRequiredService<TenancyDbContext>();
        return await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .OrderBy(t => t.Id)
            .Select(t => (long?)t.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<Guid?> TryFindCaseAsync(InspectionDbContext db, string? containerNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(containerNumber)) return null;
        return await db.Cases.AsNoTracking()
            .Where(c => c.SubjectIdentifier == containerNumber)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static System.Text.Json.Nodes.JsonObject? TryParseJsonObject(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try { return System.Text.Json.Nodes.JsonNode.Parse(payload) as System.Text.Json.Nodes.JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Wrap the raw payload string with content-hash + filename for traceability.</summary>
    private static string WrapPayload(string rawPayload, string contentHash, string fileName)
    {
        var wrapped = new System.Text.Json.Nodes.JsonObject
        {
            ["_inbox_content_hash"] = contentHash,
            ["_inbox_source_file"] = fileName,
        };
        try
        {
            // If the payload is parseable JSON, embed under _raw; else
            // store as a string under _raw_text.
            var parsed = System.Text.Json.Nodes.JsonNode.Parse(rawPayload);
            wrapped["_raw"] = parsed;
        }
        catch (System.Text.Json.JsonException)
        {
            wrapped["_raw_text"] = rawPayload;
        }
        return wrapped.ToJsonString();
    }
}

/// <summary>Telemetry instruments for <see cref="AuthorityDocumentInboxWorker"/>.</summary>
internal static class AuthorityDocumentInboxInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> IngestedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.inbox_ingested_total",
            unit: "files",
            description: "AuthorityDocumentInboxWorker count of files ingested.");

    public static readonly System.Diagnostics.Metrics.Counter<long> DedupedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.inbox_deduped_total",
            unit: "files",
            description: "AuthorityDocumentInboxWorker count of files skipped as duplicates.");

    public static readonly System.Diagnostics.Metrics.Counter<long> UnmatchedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.inbox_unmatched_total",
            unit: "files",
            description: "AuthorityDocumentInboxWorker count of files dropped because no matching case exists.");

    public static readonly System.Diagnostics.Metrics.Counter<long> IngestFailedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.inbox_failed_total",
            unit: "files",
            description: "AuthorityDocumentInboxWorker count of files that threw on ingest.");

    /// <summary>
    /// Sprint 50 / FU-inbox-multi-tenant — direct-child subdirectory of
    /// the drop root that doesn't match any active tenant code. One
    /// bump per cycle per unknown directory; surfaces operator typos
    /// without flooding logs. Tag: <c>dir_name</c>.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> UnknownTenantTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.inbox_unknown_tenant_total",
            unit: "directories",
            description: "AuthorityDocumentInboxWorker count of drop subdirs not matching any active tenant code.");
}
