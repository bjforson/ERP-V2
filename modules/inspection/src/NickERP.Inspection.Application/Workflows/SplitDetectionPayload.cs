namespace NickERP.Inspection.Application.Workflows;

/// <summary>
/// Sprint 14 / B-queues — payload carried per row on
/// <c>inspection.queue_split_detection</c>. Captures the minimal
/// reference set the (vendor-neutral) split-detection consumer needs to
/// (eventually) call into the imaging pipeline: which case the work
/// belongs to, and which image to operate on.
/// </summary>
/// <param name="CaseId">
/// Identifier of the <see cref="NickERP.Inspection.Core" /> case the
/// split-detection work was enqueued for. Stable across retries — the
/// platform's idempotency key is derived from the (CaseId, ImageRef)
/// tuple by the producer so duplicate enqueues collapse to a single row.
/// </param>
/// <param name="ImageRef">
/// Opaque storage reference for the source image the consumer should
/// inspect. Vendor-neutral by design — the actual resolution to a
/// concrete blob (FS6000 triplet, ASE pair, etc.) happens inside the
/// imaging adapter the consumer dispatches to. Format is defined by the
/// imaging layer (currently a path-style reference into the
/// <c>NickERP.Inspection.Imaging</c> assembly); the queueing layer does
/// not interpret it.
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
/// <b>Why so small.</b> Sprint 14 wires the queueing platform end-to-end
/// as a proof-of-life; the actual splitter integration (which consumes
/// imagery + writes <c>SplitStatus</c> on the case) lands in a later
/// sprint. The payload is deliberately the smallest set of fields that
/// will keep working when the real consumer body shows up — adding
/// fields later is a forward-compatible JSON change; renaming or
/// removing them is not.
/// </para>
/// </remarks>
public sealed record SplitDetectionPayload(Guid CaseId, string ImageRef);
