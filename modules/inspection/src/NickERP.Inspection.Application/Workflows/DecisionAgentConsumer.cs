using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Consumer for <c>inspection.queue_decision_agent</c>. Scores the case
/// from persisted findings, emits an auditable recommendation, and hands
/// the work item to audit assignment.
/// </summary>
public sealed class DecisionAgentConsumer : IQueueConsumer<DecisionAgentPayload>
{
    private const string EventType = "inspection.decision_agent.scored";

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ITransactionalQueue<AuditAssignmentPayload> _auditAssignmentQueue;
    private readonly ILogger<DecisionAgentConsumer> _logger;

    public DecisionAgentConsumer(
        InspectionDbContext db,
        ITenantContext tenant,
        IEventPublisher events,
        ITransactionalQueue<AuditAssignmentPayload> auditAssignmentQueue,
        ILogger<DecisionAgentConsumer> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _auditAssignmentQueue = auditAssignmentQueue ?? throw new ArgumentNullException(nameof(auditAssignmentQueue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ProcessAsync(IQueueClaim<DecisionAgentPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "DecisionAgentConsumer cannot run without a resolved tenant context.");
        }

        var caseId = claim.Payload.CaseId;
        var score = await ScoreAsync(caseId, ct).ConfigureAwait(false);

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await _auditAssignmentQueue.EnqueueAsync(
                _db,
                new EnqueueRequest<AuditAssignmentPayload>
                {
                    WorkItemId = claim.WorkItemId,
                    Payload = new AuditAssignmentPayload(claim.WorkItemId, caseId, now),
                    IdempotencyKey = IdempotencyKey.From(
                        "inspection",
                        "audit-assignment",
                        claim.WorkItemId,
                        caseId),
                    CorrelationId = claim.CorrelationId
                },
                ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        await EmitScoreEventAsync(caseId, claim.WorkItemId, score, claim.CorrelationId, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Decision agent scored CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount} Recommendation={Recommendation} FindingCount={FindingCount}; enqueued audit assignment",
            caseId,
            claim.WorkItemId,
            claim.AttemptCount,
            score.Recommendation,
            score.FindingCount);
    }

    private async Task<DecisionAgentScore> ScoreAsync(Guid caseId, CancellationToken ct)
    {
        var reviewIds = await _db.ReviewSessions.AsNoTracking()
            .Where(s => s.CaseId == caseId)
            .Join(
                _db.AnalystReviews.AsNoTracking(),
                s => s.Id,
                r => r.ReviewSessionId,
                (_, r) => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var severities = reviewIds.Count == 0
            ? new List<string>()
            : await _db.Findings.AsNoTracking()
                .Where(f => reviewIds.Contains(f.AnalystReviewId))
                .Select(f => f.Severity)
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var counts = severities
            .Select(NormalizeSeverity)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var criticalCount = Count(counts, "critical")
                          + Count(counts, "error")
                          + Count(counts, "incomplete");
        var warningCount = Count(counts, "warning")
                         + Count(counts, "partial")
                         + Count(counts, "partiallycomplete")
                         + Count(counts, "partially-complete");

        var recommendation = criticalCount > 0
            ? "refer"
            : warningCount > 0
                ? "inspect"
                : "clear";

        return new DecisionAgentScore(
            Recommendation: recommendation,
            FindingCount: severities.Count,
            SeverityCounts: counts);
    }

    private async Task EmitScoreEventAsync(
        Guid caseId,
        Guid workItemId,
        DecisionAgentScore score,
        string? correlationId,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                caseId,
                workItemId,
                recommendation = score.Recommendation,
                findingCount = score.FindingCount,
                severityCounts = score.SeverityCounts
            });
            var key = IdempotencyKey.From(
                _tenant.TenantId,
                EventType,
                workItemId,
                caseId);
            var evt = DomainEvent.Create(
                _tenant.TenantId,
                actorUserId: null,
                correlationId: correlationId,
                eventType: EventType,
                entityType: nameof(InspectionCase),
                entityId: caseId.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to emit {EventType} for case {CaseId}.",
                EventType,
                caseId);
        }
    }

    private static string NormalizeSeverity(string? severity)
        => string.IsNullOrWhiteSpace(severity)
            ? "info"
            : severity.Trim().ToLowerInvariant();

    private static int Count(IReadOnlyDictionary<string, int> counts, string key)
        => counts.TryGetValue(key, out var count) ? count : 0;

    private sealed record DecisionAgentScore(
        string Recommendation,
        int FindingCount,
        IReadOnlyDictionary<string, int> SeverityCounts);
}
