using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Consumer for <c>inspection.queue_audit_review</c>. Delegates the
/// completed audit-review disposition to the application service that owns
/// case state, verdict, and outbound-submission handoff.
/// </summary>
public sealed class AuditReviewConsumer : IQueueConsumer<AuditReviewPayload>
{
    private readonly ITenantContext _tenant;
    private readonly IAuditDispositionService _dispositions;
    private readonly ILogger<AuditReviewConsumer> _logger;

    public AuditReviewConsumer(
        ITenantContext tenant,
        IAuditDispositionService dispositions,
        ILogger<AuditReviewConsumer> logger)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _dispositions = dispositions ?? throw new ArgumentNullException(nameof(dispositions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task ProcessAsync(IQueueClaim<AuditReviewPayload> claim, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "AuditReviewConsumer cannot run without a resolved tenant context.");
        }

        var result = await _dispositions.RouteAsync(
                claim.Payload,
                claim.WorkItemId,
                claim.CorrelationId,
                ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Audit review routed for CaseId={CaseId} WorkItemId={WorkItemId} AttemptCount={AttemptCount}; Outcome={Outcome} VerdictDecision={VerdictDecision} EnqueuedSubmission={EnqueuedSubmission}",
            result.CaseId,
            claim.WorkItemId,
            claim.AttemptCount,
            result.Outcome,
            result.VerdictDecision?.ToString() ?? "none",
            result.EnqueuedSubmission);
    }
}
