using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Consumer for <c>inspection.queue_image_analysis</c>. Runs the
/// implemented analysis engines, then hands the work item to the
/// decision-agent stage.
/// </summary>
public sealed class ImageAnalysisConsumer : IQueueConsumer<ImageAnalysisPayload>
{
    private const string CompletenessEngineOutcome = "completeness-engine";

    private readonly InspectionDbContext _db;
    private readonly ValidationEngine _validationEngine;
    private readonly ICompletenessChecker _completenessChecker;
    private readonly ITransactionalQueue<DecisionAgentPayload> _decisionAgentQueue;
    private readonly ILogger<ImageAnalysisConsumer> _logger;

    public ImageAnalysisConsumer(
        InspectionDbContext db,
        ValidationEngine validationEngine,
        ICompletenessChecker completenessChecker,
        ITransactionalQueue<DecisionAgentPayload> decisionAgentQueue,
        ILogger<ImageAnalysisConsumer> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _validationEngine = validationEngine ?? throw new ArgumentNullException(nameof(validationEngine));
        _completenessChecker = completenessChecker ?? throw new ArgumentNullException(nameof(completenessChecker));
        _decisionAgentQueue = decisionAgentQueue ?? throw new ArgumentNullException(nameof(decisionAgentQueue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ProcessAsync(IQueueClaim<ImageAnalysisPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var caseId = claim.Payload.CaseId;
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var validationRan = false;
        if (!await HasValidationSnapshotAsync(caseId, ct).ConfigureAwait(false))
        {
            await _validationEngine.EvaluateAsync(caseId, ct).ConfigureAwait(false);
            validationRan = true;
        }

        var completenessRan = false;
        if (!await HasCompletenessEngineSessionAsync(caseId, ct).ConfigureAwait(false))
        {
            await _completenessChecker.EvaluateAsync(caseId, ct).ConfigureAwait(false);
            completenessRan = true;
        }

        var now = DateTimeOffset.UtcNow;
        await _decisionAgentQueue.EnqueueAsync(
                _db,
                new EnqueueRequest<DecisionAgentPayload>
                {
                    WorkItemId = claim.WorkItemId,
                    Payload = new DecisionAgentPayload(claim.WorkItemId, caseId, now),
                    IdempotencyKey = IdempotencyKey.From(
                        "inspection",
                        "decision-agent",
                        claim.WorkItemId,
                        caseId),
                    CorrelationId = claim.CorrelationId
                },
                ct)
            .ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Image analysis completed for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount}; ValidationRan={ValidationRan} CompletenessRan={CompletenessRan}; enqueued decision agent",
            caseId,
            claim.WorkItemId,
            claim.AttemptCount,
            validationRan,
            completenessRan);
    }

    private Task<bool> HasValidationSnapshotAsync(Guid caseId, CancellationToken ct)
        => _db.ValidationRuleSnapshots.AsNoTracking().AnyAsync(s => s.CaseId == caseId, ct);

    private Task<bool> HasCompletenessEngineSessionAsync(Guid caseId, CancellationToken ct)
        => _db.ReviewSessions.AsNoTracking()
            .AnyAsync(s => s.CaseId == caseId && s.Outcome == CompletenessEngineOutcome, ct);
}
