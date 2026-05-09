namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint S+3 / B-queues — payload carried per row on
/// <c>inspection.queue_audit_review</c>. Captures the minimal reference
/// set the audit-review consumer needs to record an auditor's
/// disposition (concur / disagree / hold) against an analyst's
/// findings. Real fields land alongside the consumer's actual
/// implementation in a later sprint; the placeholders below keep the
/// wire format tractable for tests + logs.
/// </summary>
/// <param name="WorkItemId">
/// The <see cref="NickERP.Platform.Queueing.Entities.WorkItem{TState}"/>
/// id this row dispatches work for. Stable across retries.
/// </param>
/// <param name="CaseId">
/// Identifier of the <see cref="NickERP.Inspection.Core" /> case the
/// audit-review work was enqueued for. Used for log correlation in the
/// Sprint S+3 placeholder body.
/// </param>
/// <param name="EnqueuedAt">
/// Wallclock at producer-side enqueue. Useful for queue-depth /
/// dwell-time observability before the platform metrics surface lands.
/// </param>
/// <remarks>
/// <para>
/// <b>Record by design.</b> v2 convention is <c>record</c> for queue
/// payloads — the platform serialiser is configured for camelCase-ish
/// JSON via <see cref="System.Text.Json.JsonSerializerDefaults.Web" />,
/// and records give us value-equality + structural deserialisation for
/// free.
/// </para>
/// <para>
/// <b>Why so small.</b> Sprint S+3 wires the queue-table + consumer
/// scaffold end-to-end as compile-clean stubs; the actual auditor
/// disposition recorder (delta-vs-analyst comparison, escalation
/// triggers, second-opinion routing) lands in a later sprint. The
/// payload is deliberately the smallest set of fields that will keep
/// working when the real consumer body shows up — adding fields later
/// is a forward-compatible JSON change; renaming or removing them is
/// not.
/// </para>
/// </remarks>
public sealed record AuditReviewPayload(Guid WorkItemId, Guid CaseId, DateTimeOffset EnqueuedAt);
