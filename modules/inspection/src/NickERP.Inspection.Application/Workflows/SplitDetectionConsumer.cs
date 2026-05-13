using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.Detection;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Consumer for the <c>inspection.queue_split_detection</c> queue.
/// Runs cross-record detection, then hands the work item to image
/// analysis.
/// <see cref="SplitDetectionPayload"/>.
/// </summary>
public sealed class SplitDetectionConsumer : IQueueConsumer<SplitDetectionPayload>
{
    private readonly InspectionDbContext _db;
    private readonly CrossRecordDetectionService _detection;
    private readonly ITransactionalQueue<ImageAnalysisPayload> _imageAnalysisQueue;
    private readonly ILogger<SplitDetectionConsumer> _logger;

    public SplitDetectionConsumer(
        InspectionDbContext db,
        CrossRecordDetectionService detection,
        ITransactionalQueue<ImageAnalysisPayload> imageAnalysisQueue,
        ILogger<SplitDetectionConsumer> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _detection = detection ?? throw new ArgumentNullException(nameof(detection));
        _imageAnalysisQueue = imageAnalysisQueue ?? throw new ArgumentNullException(nameof(imageAnalysisQueue));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ProcessAsync(IQueueClaim<SplitDetectionPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var detections = await _detection
            .ScanAndPersistAsync(claim.Payload.CaseId, ct)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var idempotencyKey = IdempotencyKey.From(
            "inspection",
            "image-analysis",
            claim.WorkItemId,
            claim.Payload.CaseId);

        await _imageAnalysisQueue.EnqueueAsync(
                _db,
                new EnqueueRequest<ImageAnalysisPayload>
                {
                    WorkItemId = claim.WorkItemId,
                    Payload = new ImageAnalysisPayload(claim.WorkItemId, claim.Payload.CaseId, now),
                    IdempotencyKey = idempotencyKey,
                    CorrelationId = claim.CorrelationId
                },
                ct)
            .ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Split detection completed for CaseId={CaseId} ImageRef={ImageRef} AttemptCount={AttemptCount} DetectionCount={DetectionCount}; enqueued image analysis for WorkItemId={WorkItemId}",
            claim.Payload.CaseId,
            claim.Payload.ImageRef,
            claim.AttemptCount,
            detections.Count,
            claim.WorkItemId);
    }
}
