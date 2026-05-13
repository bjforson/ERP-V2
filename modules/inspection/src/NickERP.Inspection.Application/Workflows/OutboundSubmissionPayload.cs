namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint S+3 / B-queues — payload carried per row on
/// <c>inspection.queue_submission</c>. Captures the minimal reference
/// set the outbound-submission consumer needs to push a case's verdict
/// to the bound <c>ExternalSystemInstance</c> (vendor-neutral; the
/// adapter resolves the concrete protocol at dispatch). Real fields
/// land alongside the consumer's actual implementation in a later
/// sprint; the placeholders below keep the wire format tractable for
/// tests + logs.
/// </summary>
/// <param name="WorkItemId">
/// The <see cref="NickERP.Platform.Queueing.Entities.WorkItem{TState}"/>
/// id this row dispatches work for. Stable across retries.
/// </param>
/// <param name="CaseId">Identifier of the case being submitted.</param>
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
/// scaffold end-to-end as compile-clean stubs; the actual outbound
/// dispatch (priority lane resolution, retry budget, signed-envelope
/// formation, ICUMS / future-authority adapter routing) lands in a
/// later sprint. The payload is deliberately the smallest set of
/// fields that will keep working when the real consumer body shows up
/// — adding fields later is a forward-compatible JSON change; renaming
/// or removing them is not.
/// </para>
/// </remarks>
public sealed record OutboundSubmissionPayload(Guid WorkItemId, Guid CaseId, DateTimeOffset EnqueuedAt)
{
    /// <summary>
    /// Exact outbound-submission row the consumer should dispatch. Kept
    /// nullable so older queue rows containing only case/work-item data can
    /// still deserialize and fall back to case/idempotency lookup.
    /// </summary>
    public Guid? OutboundSubmissionId { get; init; }

    /// <summary>External-system instance chosen by the producer.</summary>
    public Guid? ExternalSystemInstanceId { get; init; }

    /// <summary>Stable host-side adapter idempotency key.</summary>
    public string? IdempotencyKey { get; init; }
}
