using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Dispatch of a verdict back to an external system. Idempotency is
/// mandatory — the same key MUST never produce a second submission to
/// the external system, even across retries / process crashes / parallel
/// run with v1.
/// </summary>
public sealed class OutboundSubmission : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CaseId { get; set; }
    public InspectionCase? Case { get; set; }

    public Guid ExternalSystemInstanceId { get; set; }
    public ExternalSystemInstance? ExternalSystemInstance { get; set; }

    /// <summary>The payload the adapter sent to the external system, as JSON.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Stable key for at-most-once semantics. Must be deterministic for a given verdict + case + external system.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Lifecycle status — "pending", "accepted", "rejected", "error".</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Adapter-shaped response from the external system, as JSON. Null until a response arrives.</summary>
    public string? ResponseJson { get; set; }

    /// <summary>Free-form error message when <see cref="Status"/> = error.</summary>
    public string? ErrorMessage { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }

    public long TenantId { get; set; }
}
