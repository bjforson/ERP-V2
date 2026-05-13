using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Plugins;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Consumer for <c>inspection.queue_submission</c>. Dispatches a concrete
/// <see cref="OutboundSubmission"/> row to its configured external-system
/// adapter and advances the case to Submitted when accepted.
/// </summary>
public sealed class SubmissionConsumer : IQueueConsumer<OutboundSubmissionPayload>
{
    private const string EventType = "nickerp.inspection.submission_dispatched";

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPluginRegistry _plugins;
    private readonly IServiceProvider _services;
    private readonly IEventPublisher _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<SubmissionConsumer> _logger;

    public SubmissionConsumer(
        InspectionDbContext db,
        ITenantContext tenant,
        IPluginRegistry plugins,
        IServiceProvider services,
        IEventPublisher events,
        ILogger<SubmissionConsumer> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task ProcessAsync(IQueueClaim<OutboundSubmissionPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "SubmissionConsumer cannot run without a resolved tenant context.");
        }

        var submission = await ClaimSubmissionForDispatchAsync(claim.Payload, ct)
            .ConfigureAwait(false);
        if (submission is null)
        {
            _logger.LogInformation(
                "Submission queue row for CaseId={CaseId} WorkItemId={WorkItemId} was already terminal.",
                claim.Payload.CaseId,
                claim.WorkItemId);
            return;
        }

        try
        {
            await DispatchAsync(submission.Id, claim.CorrelationId, ct).ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
    }

    private async Task<OutboundSubmission?> ClaimSubmissionForDispatchAsync(
        OutboundSubmissionPayload payload,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var submission = await ResolveSubmissionAsync(payload, tracking: true, ct)
            .ConfigureAwait(false);
        if (submission is null)
        {
            throw new InvalidOperationException(
                $"No OutboundSubmission found for queued case {payload.CaseId}.");
        }

        if (submission.Status is "accepted" or "rejected")
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return null;
        }

        if (submission.Status is not ("pending" or "error" or "dispatching"))
        {
            throw new InvalidOperationException(
                $"OutboundSubmission {submission.Id} is in unsupported status '{submission.Status}'.");
        }

        submission.Status = "dispatching";
        submission.LastAttemptAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return submission;
    }

    private async Task DispatchAsync(Guid submissionId, string? correlationId, CancellationToken ct)
    {
        var submission = await _db.OutboundSubmissions
            .Include(s => s.ExternalSystemInstance)
            .FirstOrDefaultAsync(s => s.Id == submissionId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"OutboundSubmission {submissionId} not found.");

        var typeCode = submission.ExternalSystemInstance?.TypeCode
            ?? throw new InvalidOperationException(
                $"OutboundSubmission {submission.Id} has no ExternalSystemInstance loaded.");

        IExternalSystemAdapter adapter;
        try
        {
            adapter = _plugins.Resolve<IExternalSystemAdapter>("inspection", typeCode, _services);
        }
        catch (KeyNotFoundException ex)
        {
            await MarkTerminalErrorAsync(submission, $"No plugin registered for typeCode '{typeCode}'.", ct)
                .ConfigureAwait(false);
            _logger.LogWarning(ex,
                "No external-system plugin registered for typeCode {TypeCode}; submission {SubmissionId} marked error.",
                typeCode,
                submission.Id);
            await EmitDispatchedEventAsync(submission, correlationId, ct).ConfigureAwait(false);
            return;
        }

        var primaryDocRef = await _db.AuthorityDocuments.AsNoTracking()
            .Where(a => a.CaseId == submission.CaseId)
            .OrderBy(a => a.ReceivedAt)
            .Select(a => a.ReferenceNumber)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) ?? string.Empty;

        var cfg = new ExternalSystemConfig(
            submission.ExternalSystemInstanceId,
            submission.TenantId,
            submission.ExternalSystemInstance!.ConfigJson);
        var request = new OutboundSubmissionRequest(
            submission.IdempotencyKey,
            primaryDocRef,
            submission.PayloadJson);

        try
        {
            var result = await adapter.SubmitAsync(cfg, request, ct).ConfigureAwait(false);
            await PersistResultAsync(submission.Id, result, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkTransientFailureAsync(submission.Id, ex.Message, ct).ConfigureAwait(false);
            _logger.LogWarning(ex,
                "Outbound submission {SubmissionId} failed while dispatching case {CaseId}.",
                submission.Id,
                submission.CaseId);
            throw;
        }

        var saved = await _db.OutboundSubmissions.AsNoTracking()
            .FirstAsync(s => s.Id == submission.Id, ct)
            .ConfigureAwait(false);
        await EmitDispatchedEventAsync(saved, correlationId, ct).ConfigureAwait(false);
    }

    private async Task PersistResultAsync(Guid submissionId, SubmissionResult result, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var submission = await _db.OutboundSubmissions
            .FirstAsync(s => s.Id == submissionId, ct)
            .ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        submission.Status = result.Accepted ? "accepted" : "rejected";
        submission.ResponseJson = result.AuthorityResponseJson;
        submission.ErrorMessage = result.Accepted ? null : TruncateError(result.Error);
        submission.RespondedAt = now;
        submission.LastAttemptAt = now;
        submission.NextAttemptAt = null;

        if (result.Accepted)
        {
            var @case = await _db.Cases
                .FirstOrDefaultAsync(c => c.Id == submission.CaseId, ct)
                .ConfigureAwait(false);
            if (@case is not null)
            {
                @case.State = InspectionWorkflowState.Submitted;
                @case.StateEnteredAt = now;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task MarkTransientFailureAsync(Guid submissionId, string error, CancellationToken ct)
    {
        var submission = await _db.OutboundSubmissions
            .FirstAsync(s => s.Id == submissionId, ct)
            .ConfigureAwait(false);
        submission.Status = "error";
        submission.ErrorMessage = TruncateError(error);
        submission.RetryCount++;
        submission.LastAttemptAt = _clock.GetUtcNow();
        submission.NextAttemptAt = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task MarkTerminalErrorAsync(
        OutboundSubmission submission,
        string error,
        CancellationToken ct)
    {
        submission.Status = "error";
        submission.ErrorMessage = TruncateError(error);
        submission.LastAttemptAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<OutboundSubmission?> ResolveSubmissionAsync(
        OutboundSubmissionPayload payload,
        bool tracking,
        CancellationToken ct)
    {
        var q = tracking
            ? _db.OutboundSubmissions
            : _db.OutboundSubmissions.AsNoTracking();
        if (payload.OutboundSubmissionId is { } id)
        {
            return await q.FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(payload.IdempotencyKey))
        {
            return await q.FirstOrDefaultAsync(s => s.IdempotencyKey == payload.IdempotencyKey, ct)
                .ConfigureAwait(false);
        }

        if (payload.ExternalSystemInstanceId is { } instanceId)
        {
            return await q
                .Where(s => s.CaseId == payload.CaseId
                            && s.ExternalSystemInstanceId == instanceId
                            && (s.Status == "pending" || s.Status == "error"))
                .OrderByDescending(s => s.SubmittedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return await q
            .Where(s => s.CaseId == payload.CaseId
                        && (s.Status == "pending" || s.Status == "error"))
            .OrderByDescending(s => s.SubmittedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task EmitDispatchedEventAsync(
        OutboundSubmission submission,
        string? correlationId,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                submission.Id,
                submission.CaseId,
                submission.Status,
                submission.ExternalSystemInstanceId
            });
            var key = IdempotencyKey.From(
                _tenant.TenantId,
                EventType,
                submission.Id,
                submission.Status,
                submission.RespondedAt ?? submission.LastAttemptAt ?? _clock.GetUtcNow());
            var evt = DomainEvent.Create(
                _tenant.TenantId,
                actorUserId: null,
                correlationId: correlationId,
                eventType: EventType,
                entityType: nameof(OutboundSubmission),
                entityId: submission.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to emit {EventType} for outbound submission {SubmissionId}.",
                EventType,
                submission.Id);
        }
    }

    private static string? TruncateError(string? error)
        => error is null ? null : error.Length > 1900 ? error[..1900] : error;
}
