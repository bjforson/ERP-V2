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
/// Sprint 24 / B3.2 — Outbound submission result poller worker.
/// For submissions where the authority returned a deferred response
/// (i.e. <c>Status='accepted'</c> + missing
/// <see cref="OutboundSubmission.RespondedAt"/>), re-polls the authority
/// to confirm the final outcome (e.g. ICUMS returns "queued" then later
/// "approved"/"rejected"). Replaces v1 <c>ICUMSDownloadBackgroundService</c>
/// with a vendor-neutral surface.
///
/// <para>
/// <b>Adapter contract.</b> The poller calls
/// <see cref="IExternalSystemAdapter.FetchDocumentsAsync"/> with a
/// <see cref="CaseLookupCriteria.AuthorityReferenceNumber"/> matching
/// the original submission's reference; if the adapter returns
/// document(s) including the submission's idempotency key, the
/// outcome is final. This re-uses the existing FetchDocumentsAsync
/// path rather than introducing a new contract surface — adapters
/// that didn't carry deferred-response semantics simply return the
/// same docs they returned at submit-time, and the poller sees no
/// state change.
/// </para>
///
/// <para>
/// <b>State machine.</b>
/// <list type="bullet">
/// <item>Submitted (Status='accepted', RespondedAt is null) → poll
///   periodically until the authority confirms.</item>
/// <item>Final (Status='accepted' AND RespondedAt is set) → out of
///   scope, no poll.</item>
/// <item>Errored (Status='error' / 'rejected') → out of scope; operator
///   UI requeues if needed.</item>
/// </list>
/// </para>
///
/// <para>Default-disabled per Sprint 24 architectural decision.</para>
/// </summary>
public sealed class OutboundSubmissionResultPollerWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<IcumsSubmissionResultPollerOptions> _options;
    private readonly ILogger<OutboundSubmissionResultPollerWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public OutboundSubmissionResultPollerWorker(
        IServiceProvider services,
        IOptions<IcumsSubmissionResultPollerOptions> options,
        ILogger<OutboundSubmissionResultPollerWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(OutboundSubmissionResultPollerWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "OutboundSubmissionResultPollerWorker disabled via {Section}:Enabled=false; not starting.",
                IcumsSubmissionResultPollerOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "OutboundSubmissionResultPollerWorker starting — polling every {Interval}, batch limit {BatchLimit}.",
            opts.PollInterval, opts.BatchLimit);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var polled = await PollOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (polled > 0)
                {
                    _logger.LogInformation(
                        "OutboundSubmissionResultPollerWorker polled {Count} submission(s).", polled);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "OutboundSubmissionResultPollerWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One poll cycle. Walks every active tenant + the in-flight
    /// submissions for that tenant, asks the adapter for current
    /// status. Returns the count of submissions polled.
    /// </summary>
    /// <remarks>Internal so tests can drive a single cycle.</remarks>
    internal async Task<int> PollOnceAsync(CancellationToken ct)
    {
        var tenantIds = await DiscoverActiveTenantsAsync(ct);
        if (tenantIds.Count == 0) return 0;

        var totalPolled = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalPolled += await PollTenantAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OutboundSubmissionResultPollerWorker tenant={TenantId} failed; continuing.",
                    tenantId);
            }
        }
        return totalPolled;
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

    private async Task<int> PollTenantAsync(long tenantId, CancellationToken ct)
    {
        var opts = _options.Value;

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var plugins = sp.GetRequiredService<IPluginRegistry>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        tenant.SetTenant(tenantId);

        try
        {
            if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await db.Database.CloseConnectionAsync();
        }
        catch { /* best-effort */ }

        // In-flight = adapter said "yes, accepted" but hasn't yet given
        // a final RespondedAt timestamp. The dispatch worker sets
        // RespondedAt when the adapter returns synchronously — these
        // are the rows where it returned a "deferred" envelope and the
        // adapter's contract requires re-polling.
        var batch = await db.OutboundSubmissions
            .Include(s => s.ExternalSystemInstance)
            .Where(s => s.Status == "accepted" && s.RespondedAt == null)
            .OrderBy(s => s.LastAttemptAt ?? DateTimeOffset.MinValue)
            .Take(opts.BatchLimit)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        var polled = 0;
        foreach (var submission in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await PollOneAsync(plugins, sp, db, submission, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OutboundSubmissionResultPollerWorker submission {Id} poll failed; will retry next cycle.",
                    submission.Id);
                OutboundResultPollerInstruments.PollFailedTotal.Add(1,
                    new KeyValuePair<string, object?>("type_code",
                        submission.ExternalSystemInstance?.TypeCode ?? "unknown"));
            }
            polled++;
        }

        return polled;
    }

    private async Task PollOneAsync(
        IPluginRegistry plugins, IServiceProvider sp,
        InspectionDbContext db, OutboundSubmission submission, CancellationToken ct)
    {
        var typeCode = submission.ExternalSystemInstance?.TypeCode
            ?? throw new InvalidOperationException(
                $"OutboundSubmission {submission.Id} has no ExternalSystemInstance loaded.");

        IExternalSystemAdapter adapter;
        try { adapter = plugins.Resolve<IExternalSystemAdapter>("inspection", typeCode, sp); }
        catch (KeyNotFoundException) { return; }

        var cfg = new ExternalSystemConfig(
            InstanceId: submission.ExternalSystemInstanceId,
            TenantId: submission.TenantId,
            ConfigJson: submission.ExternalSystemInstance!.ConfigJson);

        // Lookup by idempotency key — the adapter recognises this as
        // "give me the status of this exact submission". Adapters that
        // don't carry idempotency-key lookup return zero docs; the
        // submission stays in flight + we try again next cycle.
        var lookup = new CaseLookupCriteria(
            ContainerNumber: null,
            VehicleVin: null,
            AuthorityReferenceNumber: submission.IdempotencyKey);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<AuthorityDocumentDto> result;
        try
        {
            result = await adapter.FetchDocumentsAsync(cfg, lookup, ct);
        }
        finally
        {
            sw.Stop();
            OutboundResultPollerInstruments.PollLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type_code", typeCode));
        }

        submission.LastAttemptAt = _clock.GetUtcNow();
        if (result.Count > 0)
        {
            // Authority confirmed — bake the response into the row +
            // close the in-flight loop.
            submission.RespondedAt = submission.LastAttemptAt;
            submission.ResponseJson = result[0].PayloadJson;
            OutboundResultPollerInstruments.PollConfirmedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", typeCode));
        }
        else
        {
            // Still pending — leave Status / RespondedAt alone, just
            // bump LastAttemptAt so operator UI can see we tried.
            OutboundResultPollerInstruments.PollPendingTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", typeCode));
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Telemetry instruments for <see cref="OutboundSubmissionResultPollerWorker"/>.</summary>
internal static class OutboundResultPollerInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> PollConfirmedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.poll_confirmed_total",
            unit: "submissions",
            description: "OutboundSubmissionResultPollerWorker count of polls that confirmed final outcome.");

    public static readonly System.Diagnostics.Metrics.Counter<long> PollPendingTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.poll_pending_total",
            unit: "submissions",
            description: "OutboundSubmissionResultPollerWorker count of polls where the authority is still working.");

    public static readonly System.Diagnostics.Metrics.Counter<long> PollFailedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.poll_failed_total",
            unit: "submissions",
            description: "OutboundSubmissionResultPollerWorker count of polls that threw.");

    public static readonly System.Diagnostics.Metrics.Histogram<double> PollLatencyMs =
        NickErpActivity.Meter.CreateHistogram<double>(
            "nickerp.inspection.outbound.poll_ms",
            unit: "ms",
            description: "OutboundSubmissionResultPollerWorker per-poll wall-clock latency.");
}
