using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
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
/// it through verbatim.
/// </para>
///
/// <para>
/// <b>Retry budget (Sprint 36 / FU-outbound-dispatch-retry).</b>
/// Transient adapter failures (network blips, authority HTTP 5xx)
/// no longer flip the row to <c>error</c> on the first try. Instead
/// the dispatcher increments
/// <see cref="OutboundSubmission.RetryCount"/>, schedules
/// <see cref="OutboundSubmission.NextAttemptAt"/> = now + exponential
/// backoff (with ±25% jitter, capped at
/// <see cref="OutboundSubmissionRetryOptions.MaxBackoff"/>) and keeps
/// the row in <c>pending</c>. Only after
/// <see cref="OutboundSubmissionRetryOptions.MaxRetries"/>
/// consecutive failures does the row flip to <c>error</c> for
/// operator triage. Audit event
/// <c>nickerp.icums.submission.retry_scheduled</c> fires per failed
/// attempt; <c>nickerp.icums.submission.retry_exhausted</c> fires on
/// the budget burn-out.
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
    private readonly IOptions<OutboundSubmissionRetryOptions> _retryOptions;
    private readonly ILogger<OutboundSubmissionDispatchWorker> _logger;
    private readonly TimeProvider _clock;
    private readonly Random _jitter;

    private readonly BackgroundServiceProbeState _probe = new();

    public OutboundSubmissionDispatchWorker(
        IServiceProvider services,
        IOptions<IcumsSubmissionDispatchOptions> options,
        IOptions<OutboundSubmissionRetryOptions> retryOptions,
        ILogger<OutboundSubmissionDispatchWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _retryOptions = retryOptions;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        // Seed the jitter RNG from the clock so deterministic-clock tests
        // can reason about the result. Real prod uses default seed which
        // is fine — jitter is per-row anyway.
        _jitter = new Random(unchecked((int)_clock.GetUtcNow().UtcTicks));
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
        //
        // Sprint 36 / FU-outbound-dispatch-retry — also filter by the
        // retry-backoff window: rows whose NextAttemptAt is in the
        // future are inside their backoff and not yet eligible. NULL =
        // never failed (or operator just requeued) → eligible immediately.
        var now = _clock.GetUtcNow();
        var batch = await db.OutboundSubmissions
            .Include(s => s.ExternalSystemInstance)
            .Where(s => s.Status == "pending"
                     && (s.NextAttemptAt == null || s.NextAttemptAt <= now))
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
                // Sprint 36 / FU-outbound-dispatch-retry — transient
                // failures schedule a retry instead of flipping straight
                // to error. The helper handles backoff + audit emission.
                await HandleTransientFailureAsync(sp, db, submission, ex, ct);
            }
            dispatched++;
        }

        return dispatched;
    }

    /// <summary>
    /// Sprint 36 / FU-outbound-dispatch-retry — record a transient adapter
    /// failure. Below the retry budget the row stays in <c>pending</c>
    /// with an exponential-backoff <see cref="OutboundSubmission.NextAttemptAt"/>;
    /// at the budget the row flips to <c>error</c> so the operator UI can
    /// requeue it. Either way the row's <see cref="OutboundSubmission.RetryCount"/>
    /// + <see cref="OutboundSubmission.LastAttemptAt"/> + <see cref="OutboundSubmission.ErrorMessage"/>
    /// are persisted, and an audit event fires
    /// (<c>nickerp.icums.submission.retry_scheduled</c> /
    /// <c>nickerp.icums.submission.retry_exhausted</c>).
    /// </summary>
    /// <remarks>Internal so tests can drive a single failure cycle.</remarks>
    internal async Task HandleTransientFailureAsync(
        IServiceProvider sp,
        InspectionDbContext db,
        OutboundSubmission submission,
        Exception ex,
        CancellationToken ct)
    {
        var retryOpts = _retryOptions.Value;
        var typeCodeTag = submission.ExternalSystemInstance?.TypeCode ?? "unknown";

        var nextRetryCount = submission.RetryCount + 1;
        var now = _clock.GetUtcNow();
        var truncatedError = TruncateError(ex.Message);

        if (nextRetryCount > retryOpts.MaxRetries)
        {
            // Budget burn-out — flip to error for operator triage.
            _logger.LogError(ex,
                "OutboundSubmissionDispatchWorker submission {Id} exhausted retry budget ({Retries}/{Max}); marking error.",
                submission.Id, submission.RetryCount, retryOpts.MaxRetries);
            submission.Status = "error";
            submission.ErrorMessage = truncatedError;
            submission.LastAttemptAt = now;
            submission.RetryCount = nextRetryCount;
            // Clear the backoff window — operator requeue resets the row.
            submission.NextAttemptAt = null;
            await db.SaveChangesAsync(ct);

            OutboundDispatchInstruments.DispatchFailedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", typeCodeTag));
            OutboundDispatchInstruments.RetryExhaustedTotal.Add(1,
                new KeyValuePair<string, object?>("type_code", typeCodeTag));

            await EmitRetryAuditAsync(sp, submission, "nickerp.icums.submission.retry_exhausted",
                truncatedError, nextAttemptAt: null, ct);
            return;
        }

        // Within budget — schedule retry.
        var backoff = ComputeBackoff(retryOpts, nextRetryCount);
        var nextAttempt = now + backoff;

        _logger.LogWarning(ex,
            "OutboundSubmissionDispatchWorker submission {Id} transient failure (attempt {Retries}/{Max}); requeuing in {Backoff} (NextAttemptAt={NextAttemptAt}).",
            submission.Id, nextRetryCount, retryOpts.MaxRetries, backoff, nextAttempt);

        submission.Status = "pending"; // explicit — keep the row eligible
        submission.ErrorMessage = truncatedError;
        submission.LastAttemptAt = now;
        submission.RetryCount = nextRetryCount;
        submission.NextAttemptAt = nextAttempt;
        await db.SaveChangesAsync(ct);

        OutboundDispatchInstruments.RetryScheduledTotal.Add(1,
            new KeyValuePair<string, object?>("type_code", typeCodeTag));

        await EmitRetryAuditAsync(sp, submission, "nickerp.icums.submission.retry_scheduled",
            truncatedError, nextAttempt, ct);
    }

    /// <summary>
    /// Compute the exponential-backoff delay for a given retry attempt,
    /// capped at <see cref="OutboundSubmissionRetryOptions.MaxBackoff"/>
    /// and perturbed by ±25% jitter so a thundering herd of pending rows
    /// doesn't all retry on the same tick.
    /// </summary>
    /// <remarks>Internal so tests can verify the curve without firing the worker.</remarks>
    internal TimeSpan ComputeBackoff(OutboundSubmissionRetryOptions opts, int retryCount)
    {
        // 2^retryCount may overflow long for absurd retry counts; clamp
        // exponent to 30 (= ~1 billion seconds, well past MaxBackoff).
        var exponent = Math.Min(retryCount, 30);
        var rawTicks = opts.BaseBackoff.Ticks * (1L << exponent);
        if (rawTicks < 0) rawTicks = long.MaxValue; // overflow safety
        var rawSpan = TimeSpan.FromTicks(rawTicks);
        if (rawSpan > opts.MaxBackoff) rawSpan = opts.MaxBackoff;

        // ±25% jitter — sample [0.75, 1.25). Lock the RNG since worker
        // cycles fan out per-tenant on the same instance.
        double jitterMultiplier;
        lock (_jitter) { jitterMultiplier = 0.75 + _jitter.NextDouble() * 0.5; }
        var jittered = TimeSpan.FromTicks((long)(rawSpan.Ticks * jitterMultiplier));
        if (jittered < TimeSpan.Zero) jittered = TimeSpan.Zero;
        return jittered;
    }

    private async Task EmitRetryAuditAsync(
        IServiceProvider sp,
        OutboundSubmission submission,
        string eventType,
        string? errorMessage,
        DateTimeOffset? nextAttemptAt,
        CancellationToken ct)
    {
        var publisher = sp.GetService<IEventPublisher>();
        if (publisher is null) return; // permissive in test wiring

        try
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["submissionId"] = submission.Id.ToString(),
                ["caseId"] = submission.CaseId.ToString(),
                ["retryCount"] = submission.RetryCount,
                ["nextAttemptAt"] = nextAttemptAt,
                ["errorMessage"] = errorMessage,
                ["typeCode"] = submission.ExternalSystemInstance?.TypeCode
            };
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(
                submission.TenantId, eventType, "OutboundSubmission",
                $"{submission.Id}:{submission.RetryCount}", _clock.GetUtcNow());
            var evt = DomainEvent.Create(
                tenantId: submission.TenantId,
                actorUserId: null,
                correlationId: null,
                eventType: eventType,
                entityType: "OutboundSubmission",
                entityId: submission.Id.ToString(),
                payload: json,
                idempotencyKey: key);
            await publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Audit failure must not derail the retry loop — log + carry on.
            _logger.LogWarning(ex,
                "OutboundSubmissionDispatchWorker failed to emit {EventType} for submission {Id}.",
                eventType, submission.Id);
        }
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
        // Sprint 36 / FU-outbound-dispatch-retry — terminal-state success
        // or rejection clears the backoff window so the row's
        // NextAttemptAt is no longer pertinent.
        submission.NextAttemptAt = null;
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

    /// <summary>
    /// Sprint 36 / FU-outbound-dispatch-retry — count of transient failures
    /// that scheduled an exponential-backoff retry (status held at
    /// <c>pending</c>; <c>NextAttemptAt</c> set). Tag: <c>type_code</c>.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> RetryScheduledTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.retry_scheduled_total",
            unit: "submissions",
            description: "OutboundSubmissionDispatchWorker count of transient failures that scheduled a retry.");

    /// <summary>
    /// Sprint 36 / FU-outbound-dispatch-retry — count of submissions that
    /// burned through the retry budget and flipped to <c>error</c>. Tag:
    /// <c>type_code</c>.
    /// </summary>
    public static readonly System.Diagnostics.Metrics.Counter<long> RetryExhaustedTotal =
        NickErpActivity.Meter.CreateCounter<long>(
            "nickerp.inspection.outbound.retry_exhausted_total",
            unit: "submissions",
            description: "OutboundSubmissionDispatchWorker count of submissions that exhausted the retry budget.");
}
