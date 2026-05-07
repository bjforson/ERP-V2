using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Queueing.Entities;

/// <summary>
/// Append-only audit row written by <c>WorkItemStateMachine&lt;TState,TTrigger&gt;</c>
/// on every successful state transition. One row per transition; rows are
/// never updated or deleted.
/// </summary>
/// <remarks>
/// <para>
/// State + trigger are stored as <b>strings</b> (the enum names) rather
/// than ints so the audit log remains human-readable across schema
/// changes — adding a new state value never requires an audit-row
/// migration. The cost is a few bytes per row; the table is append-only
/// so write amplification is negligible.
/// </para>
/// <para>
/// <b>Schema is uniform across modules.</b> Each module owns its own
/// physical table (e.g. <c>inspection.work_item_transitions</c>,
/// <c>finance.work_item_transitions</c>) so the audit trail lives next
/// to the work items it describes; the CLR shape is shared from this
/// assembly so reporting code can union across modules.
/// </para>
/// <para>
/// <b>Reason field.</b> Always populated by the state machine — even
/// system-driven transitions write a reason like <c>"lease-expired"</c>
/// or <c>"agent-confidence-above-threshold"</c>. Empty reason is a bug,
/// not a feature; the platform-tests assertion <c>actor IS NOT NULL AND
/// reason &lt;&gt; ''</c> guards against it.
/// </para>
/// </remarks>
public class WorkItemTransition : ITenantOwned
{
    /// <summary>Stable primary key — bigserial so high-volume modules don't run out.</summary>
    public long Id { get; set; }

    /// <inheritdoc />
    public long TenantId { get; set; }

    /// <summary>FK to the <see cref="WorkItem{TState}"/> this transition belongs to.</summary>
    public Guid WorkItemId { get; set; }

    /// <summary>Enum name of the state the work item left. Empty string for the initial creation transition.</summary>
    public string FromState { get; set; } = string.Empty;

    /// <summary>Enum name of the state the work item moved into.</summary>
    public string ToState { get; set; } = string.Empty;

    /// <summary>Enum name of the trigger that drove the transition. e.g. <c>"AnalystSubmittedFindings"</c>.</summary>
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Who or what initiated the transition. User id (Guid as string) for
    /// human-driven transitions; service name (e.g.
    /// <c>"DECISION-AGENT"</c>, <c>"QueueJanitor"</c>,
    /// <c>"OutboxRelay"</c>) for system-driven ones. Never empty.
    /// </summary>
    public string Actor { get; set; } = string.Empty;

    /// <summary>
    /// Free-text justification (≤500 chars). For system actors this is
    /// usually a short tag like <c>"lease-expired"</c>; for human actors
    /// it's whatever the UI captured. Never empty — the state machine
    /// requires the caller to provide one.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>UTC moment the transition occurred. DB default <c>now()</c>.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// Optional correlation id propagated from the request that drove the
    /// transition (the <c>X-Correlation-Id</c> header / Serilog
    /// <c>correlation-id</c> property). Lets ops trace a transition
    /// back to the originating HTTP request, scanner event, or scheduled
    /// job run.
    /// </summary>
    public string? CorrelationId { get; set; }
}
