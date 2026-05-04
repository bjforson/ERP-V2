using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 24 / B3.2 — Authority document backfill worker.
/// Periodic per-tenant pull of authority documents (BOE, CMR, IM, Manifest)
/// from every configured <see cref="ExternalSystemInstance"/>. Replaces v1
/// <c>IcumBackgroundService</c> with a vendor-neutral periodic backfill;
/// vendor names live only in the adapter's <c>TypeCode</c>.
///
/// <para>
/// <b>Why this is distinct from <see cref="OutcomePullWorker"/>.</b> The
/// post-hoc outcome worker (Sprint 13 / §6.11) pulls authority decisions
/// after a case is closed. This worker fills the inverse path — fetching
/// authority documents for active cases that haven't yet linked one (e.g.
/// because the document landed at the authority after the scan came
/// through, or the case was opened with no manifest reference). It runs
/// for every external-system instance that has
/// <see cref="ExternalSystemCapabilities.SupportsBulkFetch"/> = true.
/// </para>
///
/// <para>
/// <b>Ingestion shape.</b> The worker iterates every active
/// <see cref="ExternalSystemInstance"/>, asks the adapter for a recent
/// document set via <see cref="IExternalSystemAdapter.FetchDocumentsAsync"/>
/// with empty <see cref="CaseLookupCriteria"/> (= "give me what's new"),
/// and inserts the resulting <see cref="AuthorityDocument"/> rows
/// against any matching cases keyed on <see cref="ReferenceNumber"/>.
/// Idempotency is via the existing
/// <c>(TenantId, ExternalSystemInstanceId, DocumentType, ReferenceNumber)</c>
/// uniqueness path — duplicates are silently dropped.
/// </para>
///
/// <para>
/// Default-disabled per Sprint 24 architectural decision. Mirrors
/// <see cref="OutcomePullWorker"/>'s shape: cross-tenant discovery,
/// per-iteration scope, plugin singleton + scoped DbContext.
/// </para>
/// </summary>
public sealed class AuthorityDocumentBackfillWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<IcumsApiPullOptions> _options;
    private readonly ILogger<AuthorityDocumentBackfillWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public AuthorityDocumentBackfillWorker(
        IServiceProvider services,
        IOptions<IcumsApiPullOptions> options,
        ILogger<AuthorityDocumentBackfillWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(AuthorityDocumentBackfillWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "AuthorityDocumentBackfillWorker disabled via {Section}:Enabled=false; not starting.",
                IcumsApiPullOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "AuthorityDocumentBackfillWorker starting — fetching every {Interval}, window overlap {Overlap}.",
            opts.PollInterval, opts.WindowOverlap);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var fetched = await BackfillOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (fetched > 0)
                {
                    _logger.LogInformation(
                        "AuthorityDocumentBackfillWorker cycle fetched {Count} document(s).", fetched);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "AuthorityDocumentBackfillWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One backfill cycle. Walks every active tenant + every active
    /// <see cref="ExternalSystemInstance"/>, asks the adapter for recent
    /// documents, persists them. Returns the count of documents inserted.
    /// </summary>
    /// <remarks>
    /// Internal so the test project can drive a single cycle without
    /// the full hosted-service start dance.
    /// </remarks>
    internal async Task<int> BackfillOnceAsync(CancellationToken ct)
    {
        var instances = await DiscoverActiveInstancesAsync(ct);
        if (instances.Count == 0) return 0;

        var totalFetched = 0;
        foreach (var instance in instances)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalFetched += await BackfillOneAsync(instance, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AuthorityDocumentBackfillInstruments.BackfillFailedTotal.Add(1,
                    new KeyValuePair<string, object?>("type_code", instance.TypeCode));
                _logger.LogError(ex,
                    "AuthorityDocumentBackfillWorker fetch failed for tenant={TenantId} instance={InstanceId} type={TypeCode}; continuing.",
                    instance.TenantId, instance.InstanceId, instance.TypeCode);
            }
        }

        return totalFetched;
    }

    private async Task<IReadOnlyList<InstanceDescriptor>> DiscoverActiveInstancesAsync(CancellationToken ct)
    {
        var results = new List<InstanceDescriptor>();

        using var discoveryScope = _services.CreateScope();
        var sp = discoveryScope.ServiceProvider;
        var tenantsDb = sp.GetRequiredService<TenancyDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();
        var inspectionDb = sp.GetRequiredService<InspectionDbContext>();

        var activeTenantIds = await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in activeTenantIds)
        {
            ct.ThrowIfCancellationRequested();
            tenant.SetTenant(tenantId);

            try
            {
                if (inspectionDb.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                    await inspectionDb.Database.CloseConnectionAsync();
            }
            catch { /* best-effort */ }

            var instances = await inspectionDb.ExternalSystemInstances
                .AsNoTracking()
                .Where(e => e.IsActive)
                .Select(e => new InstanceDescriptor(
                    tenantId,
                    e.Id,
                    e.TypeCode,
                    e.ConfigJson))
                .ToListAsync(ct);

            results.AddRange(instances);
        }

        return results;
    }

    private async Task<int> BackfillOneAsync(InstanceDescriptor instance, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var plugins = sp.GetRequiredService<IPluginRegistry>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        tenant.SetTenant(instance.TenantId);

        IExternalSystemAdapter adapter;
        try { adapter = plugins.Resolve<IExternalSystemAdapter>("inspection", instance.TypeCode, sp); }
        catch (KeyNotFoundException)
        {
            _logger.LogDebug(
                "AuthorityDocumentBackfillWorker: no plugin for typeCode={TypeCode}; skipping (tenant={TenantId} instance={InstanceId}).",
                instance.TypeCode, instance.TenantId, instance.InstanceId);
            return 0;
        }
        catch (InvalidOperationException) { return 0; }

        if (!adapter.Capabilities.SupportsBulkFetch)
        {
            // Adapter doesn't support bulk-fetch — that's the workflow
            // for case-by-case lookups (FU-7). Skip silently.
            return 0;
        }

        var cfg = new ExternalSystemConfig(
            InstanceId: instance.InstanceId,
            TenantId: instance.TenantId,
            ConfigJson: instance.ConfigJson);

        // Empty lookup criteria = "give me what's new". Adapters interpret
        // this against their own recency semantics; the host de-dupes via
        // (Tenant, Instance, DocType, ReferenceNumber) below.
        var lookup = new CaseLookupCriteria(null, null, null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<AuthorityDocumentDto> fetched;
        try
        {
            fetched = await adapter.FetchDocumentsAsync(cfg, lookup, ct);
        }
        finally
        {
            sw.Stop();
            AuthorityDocumentBackfillInstruments.FetchLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type_code", instance.TypeCode));
        }

        if (fetched.Count == 0) return 0;

        // De-dupe by (tenant, instance, doctype, refnum). Bulk-load the
        // existing references so we can write only new rows. Cheap for the
        // typical "few new rows per cycle" workload; future improvement is
        // a unique index + ON CONFLICT DO NOTHING.
        var refs = fetched.Select(d => d.ReferenceNumber).ToList();
        var existing = await db.AuthorityDocuments.AsNoTracking()
            .Where(a => a.ExternalSystemInstanceId == instance.InstanceId
                        && refs.Contains(a.ReferenceNumber))
            .Select(a => new { a.DocumentType, a.ReferenceNumber })
            .ToListAsync(ct);
        var existingSet = new HashSet<(string DocumentType, string ReferenceNumber)>(
            existing.Select(e => (e.DocumentType, e.ReferenceNumber)));

        // Match the adapter's documents to existing cases by container
        // number (best effort — the case keying contract is intentionally
        // loose here; cases without a match are persisted with a placeholder
        // CaseId so the analyst-side admin UIs can re-link them later).
        var inserted = 0;
        foreach (var dto in fetched)
        {
            ct.ThrowIfCancellationRequested();
            if (existingSet.Contains((dto.DocumentType, dto.ReferenceNumber))) continue;

            var caseId = await TryFindMatchingCaseAsync(db, dto, ct);
            if (caseId is null)
            {
                // No matching case yet — skip; the matcher worker will
                // pick this up when the scan eventually arrives. We don't
                // create a phantom case here.
                AuthorityDocumentBackfillInstruments.UnmatchedTotal.Add(1,
                    new KeyValuePair<string, object?>("type_code", instance.TypeCode));
                continue;
            }

            db.AuthorityDocuments.Add(new AuthorityDocument
            {
                Id = Guid.NewGuid(),
                CaseId = caseId.Value,
                ExternalSystemInstanceId = instance.InstanceId,
                DocumentType = dto.DocumentType,
                ReferenceNumber = dto.ReferenceNumber,
                PayloadJson = dto.PayloadJson,
                ReceivedAt = dto.ReceivedAt,
                TenantId = instance.TenantId
            });
            inserted++;
        }

        if (inserted > 0)
        {
            await db.SaveChangesAsync(ct);
            AuthorityDocumentBackfillInstruments.InsertedTotal.Add(inserted,
                new KeyValuePair<string, object?>("type_code", instance.TypeCode));
        }

        return inserted;
    }

    /// <summary>
    /// Best-effort case lookup for an inbound document. Today we only
    /// match on container number (the most common authority key); the
    /// matcher worker handles richer cross-record correlation. Returns
    /// null when no match exists — the document is dropped and the
    /// next cycle re-evaluates.
    /// </summary>
    private static async Task<Guid?> TryFindMatchingCaseAsync(
        InspectionDbContext db, AuthorityDocumentDto dto, CancellationToken ct)
    {
        // Adapter-shaped payload may carry a container_id / container_number
        // — extract via the same parse path the OutcomePullWorker uses.
        // Without one, we cannot resolve a case.
        var containerNumber = ExtractContainerNumber(dto.PayloadJson);
        if (string.IsNullOrWhiteSpace(containerNumber)) return null;

        var caseId = await db.Cases.AsNoTracking()
            .Where(c => c.SubjectIdentifier == containerNumber)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        return caseId;
    }

    private static string? ExtractContainerNumber(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payloadJson) as System.Text.Json.Nodes.JsonObject;
            if (node is null) return null;
            return node["container_number"]?.GetValue<string>()
                ?? node["container_id"]?.GetValue<string>()
                ?? node["ContainerNumber"]?.GetValue<string>();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private sealed record InstanceDescriptor(
        long TenantId, Guid InstanceId, string TypeCode, string ConfigJson);
}

/// <summary>Telemetry instruments for <see cref="AuthorityDocumentBackfillWorker"/>.</summary>
internal static class AuthorityDocumentBackfillInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> InsertedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.backfill_inserted_total",
            unit: "documents",
            description: "AuthorityDocumentBackfillWorker count of documents inserted.");

    public static readonly System.Diagnostics.Metrics.Counter<long> UnmatchedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.backfill_unmatched_total",
            unit: "documents",
            description: "AuthorityDocumentBackfillWorker count of documents with no matching case (held for matcher).");

    public static readonly System.Diagnostics.Metrics.Counter<long> BackfillFailedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.authority_doc.backfill_failed_total",
            unit: "calls",
            description: "AuthorityDocumentBackfillWorker count of failed FetchDocuments calls.");

    public static readonly System.Diagnostics.Metrics.Histogram<double> FetchLatencyMs =
        NickErpActivity.Meter.CreateHistogram<double>(
            "nickerp.inspection.authority_doc.backfill_fetch_ms",
            unit: "ms",
            description: "AuthorityDocumentBackfillWorker per-fetch wall-clock latency.");
}
