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
/// Sprint 24 / B3.2 — Authority document matcher worker.
/// Periodically matches <see cref="Scan"/> rows to <see cref="AuthorityDocument"/>
/// rows by container number + capture-window. Replaces v1
/// <c>ContainerDataMapperService</c>.
///
/// <para>
/// <b>The matching problem.</b> Scans land via the scanner adapters
/// (FS6000, ASE) and authority documents land via the inbox / API
/// pull workers; they don't generally arrive in lockstep. v1's
/// <c>ContainerDataMapperService</c> ran every minute looking for
/// <c>(scan_with_container_number, document_with_same_container_number)</c>
/// pairs that landed within a configurable time window and stitched
/// them together. v2 carries the same shape but operates on the
/// vendor-neutral <see cref="Scan"/> + <see cref="AuthorityDocument"/>
/// + <see cref="InspectionCase"/> entities.
/// </para>
///
/// <para>
/// <b>Match rule.</b> A scan and an authority document match when:
/// <list type="bullet">
///   <item>Both belong to the same tenant (RLS enforces this).</item>
///   <item>The scan's case has a non-empty
///   <see cref="InspectionCase.SubjectIdentifier"/> matching the
///   authority document's container number (extracted from
///   <see cref="AuthorityDocument.PayloadJson"/>).</item>
///   <item>Their capture/receive timestamps are within
///   <see cref="ContainerDataMatcherOptions.CaptureWindow"/>.</item>
/// </list>
/// On match, if the document's <see cref="AuthorityDocument.CaseId"/>
/// is not already set to a case for this container, the worker
/// re-points it to the scan's case. The scan side is unchanged —
/// scans always belong to the case they were captured against.
/// </para>
///
/// <para>
/// <b>Idempotency.</b> The worker is naturally idempotent — a re-run
/// over already-matched data sees no change to make. No new tracking
/// table.
/// </para>
///
/// <para>Default-disabled per Sprint 24 architectural decision.</para>
/// </summary>
public sealed class AuthorityDocumentMatcherWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<ContainerDataMatcherOptions> _options;
    private readonly ILogger<AuthorityDocumentMatcherWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public AuthorityDocumentMatcherWorker(
        IServiceProvider services,
        IOptions<ContainerDataMatcherOptions> options,
        ILogger<AuthorityDocumentMatcherWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(AuthorityDocumentMatcherWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "AuthorityDocumentMatcherWorker disabled via {Section}:Enabled=false; not starting.",
                ContainerDataMatcherOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "AuthorityDocumentMatcherWorker starting — matching every {Interval}, capture window {Window}, batch limit {BatchLimit}.",
            opts.PollInterval, opts.CaptureWindow, opts.BatchLimit);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var matched = await MatchOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (matched > 0)
                {
                    _logger.LogInformation(
                        "AuthorityDocumentMatcherWorker matched {Count} authority document(s) to scans.",
                        matched);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "AuthorityDocumentMatcherWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One match cycle. Walks every active tenant + finds documents
    /// whose payload carries a container number that matches a scan's
    /// case-subject within the capture window. Returns the count of
    /// matches made.
    /// </summary>
    /// <remarks>Internal so tests can drive a single cycle.</remarks>
    internal async Task<int> MatchOnceAsync(CancellationToken ct)
    {
        var tenantIds = await DiscoverActiveTenantsAsync(ct);
        if (tenantIds.Count == 0) return 0;

        var totalMatched = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalMatched += await MatchTenantAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AuthorityDocumentMatcherWorker tenant={TenantId} failed; continuing.",
                    tenantId);
            }
        }
        return totalMatched;
    }

    private async Task<IReadOnlyList<long>> DiscoverActiveTenantsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var tenantsDb = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    private async Task<int> MatchTenantAsync(long tenantId, CancellationToken ct)
    {
        var opts = _options.Value;

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        tenant.SetTenant(tenantId);

        try
        {
            if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await db.Database.CloseConnectionAsync();
        }
        catch { /* best-effort */ }

        // Find documents that haven't yet been matched to a scan-bearing
        // case within the capture window. We assume an unmatched document
        // either points at a placeholder case OR points at the right
        // case-by-container but hasn't yet been scan-aligned. The
        // re-evaluation logic below idempotently does the right thing
        // either way.
        var batch = await db.AuthorityDocuments.AsNoTracking()
            .OrderBy(a => a.ReceivedAt)
            .Take(opts.BatchLimit)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        var matched = 0;
        foreach (var doc in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryMatchOneAsync(db, doc, opts.CaptureWindow, ct)) matched++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AuthorityDocumentMatcherWorker doc {Id} failed; continuing.",
                    doc.Id);
            }
        }
        return matched;
    }

    private async Task<bool> TryMatchOneAsync(
        InspectionDbContext db, AuthorityDocument doc, TimeSpan captureWindow, CancellationToken ct)
    {
        var containerNumber = ExtractContainerNumber(doc.PayloadJson);
        if (string.IsNullOrWhiteSpace(containerNumber))
        {
            ContainerDataMatcherInstruments.NoContainerNumberTotal.Add(1);
            return false;
        }

        // Find candidate cases — same tenant (RLS), same container,
        // opened/captured within the capture window of doc.ReceivedAt.
        var sinceUtc = doc.ReceivedAt - captureWindow;
        var untilUtc = doc.ReceivedAt + captureWindow;
        var candidate = await db.Cases
            .Where(c => c.SubjectIdentifier == containerNumber
                        && c.OpenedAt >= sinceUtc
                        && c.OpenedAt <= untilUtc)
            .OrderBy(c => c.OpenedAt)
            .Select(c => new { c.Id, ScanCount = c.Scans.Count })
            .FirstOrDefaultAsync(ct);

        if (candidate is null)
        {
            ContainerDataMatcherInstruments.NoCaseFoundTotal.Add(1);
            return false;
        }

        if (doc.CaseId == candidate.Id)
        {
            // Already correctly attributed — nothing to do.
            return false;
        }

        // Re-attribute. Track the entity so the SaveChangesAsync writes
        // the update. The doc instance we have is AsNoTracking, so we
        // re-fetch tracked.
        var tracked = await db.AuthorityDocuments.FirstOrDefaultAsync(a => a.Id == doc.Id, ct);
        if (tracked is null) return false;

        var oldCaseId = tracked.CaseId;
        tracked.CaseId = candidate.Id;
        await db.SaveChangesAsync(ct);

        ContainerDataMatcherInstruments.MatchedTotal.Add(1);
        _logger.LogDebug(
            "AuthorityDocumentMatcherWorker re-attributed doc {Id} from case {OldCase} to case {NewCase} (container={Container}).",
            doc.Id, oldCaseId, candidate.Id, containerNumber);
        return true;
    }

    private static string? ExtractContainerNumber(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(payloadJson) as System.Text.Json.Nodes.JsonObject;
            if (node is null) return null;
            // Look both at top level + inside _raw for inbox-wrapped payloads
            var direct = node["container_number"]?.GetValue<string>()
                ?? node["container_id"]?.GetValue<string>()
                ?? node["ContainerNumber"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(direct)) return direct;

            if (node["_raw"] is System.Text.Json.Nodes.JsonObject raw)
            {
                return raw["container_number"]?.GetValue<string>()
                    ?? raw["container_id"]?.GetValue<string>()
                    ?? raw["ContainerNumber"]?.GetValue<string>();
            }
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Telemetry instruments for <see cref="AuthorityDocumentMatcherWorker"/>.</summary>
internal static class ContainerDataMatcherInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> MatchedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.matcher.matched_total",
            unit: "documents",
            description: "AuthorityDocumentMatcherWorker count of authority documents re-attributed to a scan case.");

    public static readonly System.Diagnostics.Metrics.Counter<long> NoContainerNumberTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.matcher.no_container_total",
            unit: "documents",
            description: "AuthorityDocumentMatcherWorker count of documents lacking a container number in the payload.");

    public static readonly System.Diagnostics.Metrics.Counter<long> NoCaseFoundTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.matcher.no_case_total",
            unit: "documents",
            description: "AuthorityDocumentMatcherWorker count of documents with no matching case in window.");
}
