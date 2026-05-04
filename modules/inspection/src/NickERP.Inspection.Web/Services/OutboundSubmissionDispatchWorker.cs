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
/// Sprint 24 / B3.2 — Outbound submission dispatch worker.
/// Reads <see cref="OutboundSubmission"/> rows in <c>Status='pending'</c>
/// and dispatches them to the corresponding
/// <see cref="ExternalSystemInstance"/> via the adapter's
/// <see cref="IExternalSystemAdapter.SubmitAsync"/> path. Replaces v1
/// <c>ICUMSSubmissionService</c>; vendor-neutralised.
///
/// <para>
/// <b>Priority + LastAttempt ordering.</b> Sprint 22 / B2.1 added
/// <see cref="OutboundSubmission.Priority"/> + <see cref="OutboundSubmission.LastAttemptAt"/>;
/// the dispatcher orders by Priority DESC, then SubmittedAt ASC, then
/// LastAttemptAt NULLS FIRST. That gives "high priority first; then
/// FIFO; then never-tried first" semantics.
/// </para>
///
/// <para>
/// <b>Idempotency.</b> The
/// <see cref="OutboundSubmission.IdempotencyKey"/> is the contract;
/// adapters MUST guarantee at-most-once per key. The worker passes
/// it through verbatim. Retries (failed Status='error' with retry
/// budget remaining) are out of scope for v0; today, errors stay
/// errors and operator UI requeues them.
/// </para>
///
/// <para>
/// Default-disabled per Sprint 24 architectural decision.
/// </para>
/// </summary>
public sealed class OutboundSubmissionDispatchWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<IcumsSubmissionDispatchOptions> _options;
    private readonly ILogger<OutboundSubmissionDispatchWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public OutboundSubmissionDispatchWorker(
        IServiceProvider services,
        IOptions<IcumsSubmissionDispatchOptions> options,
        ILogger<OutboundSubmissionDispatchWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(OutboundSubmissionDispatchWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "OutboundSubmissionDispatchWorker disabled via {Section}:Enabled=false; not starting.",
                IcumsSubmissionDispatchOptions.SectionName);
            return;
        }

        _probe.SetPollInterval(opts.PollInterval);
        _logger.LogInformation(
            "OutboundSubmissionDispatchWorker starting — dispatching every {Interval}, batch limit {BatchLimit}.",
            opts.PollInterval, opts.BatchLimit);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var dispatched = await DispatchOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                if (dispatched > 0)
                {
                    _logger.LogInformation(
                        "OutboundSubmissionDispatchWorker dispatched {Count} submission(s).", dispatched);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "OutboundSubmissionDispatchWorker cycle failed; will retry in {Interval}.",
                    opts.PollInterval);
            }

            try { await Task.Delay(opts.PollInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One dispatch cycle. Walks every active tenant, fetches up to
    /// <see cref="IcumsSubmissionDispatchOptions.BatchLimit"/> pending
    /// submissions, dispatches each via the matching adapter. Returns
    /// the count of dispatch attempts (success + failure).
    /// </summary>
    /// <remarks>Internal so tests can drive a single cycle.</remarks>
    internal async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var tenantIds = await DiscoverActiveTenantsAsync(ct);
        if (tenantIds.Count == 0) return 0;

        var totalDispatched = 0;
        foreach (var tenantId in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                totalDispatched += await DispatchTenantAsync(tenantId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OutboundSubmissionDispatchWorker failed for tenant={TenantId}; continuing.",
                    tenantId);
            }
        }
        return totalDispatched;
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

    private async Task<int> DispatchTenantAsync(long tenantId, CancellationToken ct)
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

        // Pull the next batch with the priority + LastAttempt ordering
        // contract from Sprint 22 / B2.1. The Include() + projection avoids
        // a second round-trip for the typeCode.
        var batch = await db.OutboundSubmissions
            .Include(s => s.ExternalSystemInstance)
            .Where(s => s.Status == "pending")
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.SubmittedAt)
            .ThenBy(s => s.LastAttemptAt ?? DateTimeOffset.MinValue)
            .Take(opts.BatchLimit)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        var dispatched = 0;
        foreach (var submission in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DispatchOneAsync(plugins, sp, db, submission, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OutboundSubmissionDispatchWorker submission {Id} failed; marking error.",
                    submission.Id);
                submission.Status = "error";
                submission.ErrorMessage = TruncateError(ex.Message);
                submission.LastAttemptAt = _clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
                OutboundDispatchInstruments.DispatchFailedTotal.Add(1,
                    new KeyValuePair<string, object?>("type_code",
                        submission.ExternalSystemInstance?.TypeCode ?? "unknown"));
            }
            dispatched++;
        }

        return dispatched;
    }

    private async Task DispatchOneAsync(
        IPluginRegistry plugins, IServiceProvider sp,
        InspectionDbContext db, OutboundSubmission submission, CancellationToken ct)
    {
        var typeCode = submission.ExternalSystemInstance?.TypeCode
            ?? throw new InvalidOperationException(
                $"OutboundSubmission {submission.Id} has no ExternalSystemInstance loaded.");

        IExternalSystemAdapter adapter;
        try { adapter = plugins.Resolve<IExternalSystemAdapter>("inspection", typeCode, sp); }
        catch (KeyNotFoundException)
        {
            submission.Status = "error";
            submission.ErrorMessage = $"No plugin registered for typeCode '{typeCode}'.";
            submission.LastAttemptAt = _clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return;
        }

        var cfg = new ExternalSystemConfig(
            InstanceId: submission.ExternalSystemInstanceId,
            TenantId: submission.TenantId,
            ConfigJson: submission.ExternalSystemInstance!.ConfigJson);

        // Use the case's primary AuthorityDocument reference number when
        // available (the v1 contract). Fallback: empty string — adapters
        // that need a reference fail with a clear error.
        var primaryDocRef = await db.AuthorityDocuments.AsNoTracking()
            .Where(a => a.CaseId == submission.CaseId)
            .OrderBy(a => a.ReceivedAt)
            .Select(a => a.ReferenceNumber)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var request = new OutboundSubmissionRequest(
            IdempotencyKey: submission.IdempotencyKey,
            AuthorityReferenceNumber: primaryDocRef,
            PayloadJson: submission.PayloadJson);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        SubmissionResult result;
        try
        {
            result = await adapter.SubmitAsync(cfg, request, ct);
        }
        finally
        {
            sw.Stop();
            OutboundDispatchInstruments.DispatchLatencyMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type_code", typeCode));
        }

        submission.LastAttemptAt = _clock.GetUtcNow();
        if (result.Accepted)
        {
            submission.Status = "accepted";
            submission.ResponseJson = result.AuthorityResponseJson;
            submission.RespondedAt = submission.LastAttemptAt;
            OutboundDispatchInstruments.DispatchAcceptedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", typeCode));
        }
        else
        {
            submission.Status = "rejected";
            submission.ResponseJson = result.AuthorityResponseJson;
            submission.ErrorMessage = TruncateError(result.Error);
            submission.RespondedAt = submission.LastAttemptAt;
            OutboundDispatchInstruments.DispatchRejectedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", typeCode));
        }
        await db.SaveChangesAsync(ct);
    }

    private static string? TruncateError(string? error) =>
        error is null ? null : error.Length > 1900 ? error[..1900] : error;
}

/// <summary>Telemetry instruments for <see cref="OutboundSubmissionDispatchWorker"/>.</summary>
internal static class OutboundDispatchInstruments
{
    public static readonly System.Diagnostics.Metrics.Counter<long> DispatchAcceptedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.dispatch_accepted_total",
            unit: "submissions",
            description: "OutboundSubmissionDispatchWorker count of submissions the authority accepted.");

    public static readonly System.Diagnostics.Metrics.Counter<long> DispatchRejectedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.dispatch_rejected_total",
            unit: "submissions",
            description: "OutboundSubmissionDispatchWorker count of submissions the authority rejected.");

    public static readonly System.Diagnostics.Metrics.Counter<long> DispatchFailedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.dispatch_failed_total",
            unit: "submissions",
            description: "OutboundSubmissionDispatchWorker count of submissions that threw on dispatch.");

    public static readonly System.Diagnostics.Metrics.Histogram<double> DispatchLatencyMs =
        NickErpActivity.Meter.CreateHistogram<double>(
            "nickerp.inspection.outbound.dispatch_ms",
            unit: "ms",
            description: "OutboundSubmissionDispatchWorker per-submission wall-clock latency.");
}
