using NickERP.Platform.Queueing.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Concrete <see cref="WorkItem{TState}"/> for the inspection module —
/// pairs the platform queueing substrate's cross-cutting fields (id,
/// tenancy, current state, version, idempotency anchor, timestamps) with
/// the inspection-specific FK back to the <see cref="InspectionCase"/>
/// the work flows for. Subclasses <see cref="WorkItem{TState}"/> closed
/// over <see cref="InspectionWorkflowState"/> so the
/// <c>InspectionStateMachine</c> can read/write its
/// <see cref="WorkItem{TState}.CurrentState"/> with compile-time-safe
/// triggers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Workflow discriminator.</b> The base's
/// <see cref="WorkItem{TState}.Workflow"/> string is initialised to
/// <see cref="WorkflowName"/> in the parameterless constructor — every
/// row inserted through this entity carries
/// <c>Workflow = "inspection"</c>, so a future cross-module timeline view
/// can union work items across modules and still tell which workflow
/// each row belongs to.
/// </para>
/// <para>
/// <b>FK to <see cref="InspectionCase"/>.</b> One work item per case is
/// the typical pattern (the case is the atom of inspection work), but
/// the relationship is unidirectional — the case entity is unaware of
/// the work-item layer to keep the legacy
/// <c>CaseWorkflowService</c> path's surface unchanged during cutover.
/// The state machine layer (this entity + <c>InspectionStateMachine</c>)
/// runs alongside the legacy path until cutover; both edit
/// <see cref="InspectionCase"/> only via their respective sole writers.
/// </para>
/// <para>
/// <b>Sole-writer discipline.</b> Inherited from
/// <see cref="WorkItem{TState}"/>:
/// <see cref="WorkItem{TState}.CurrentState"/>'s setter is
/// <c>internal</c>, only the queueing assembly's
/// <c>WorkItemStateMachine&lt;TState,TTrigger,TWorkItem&gt;</c> can write
/// it. Inspection code reads it; it never assigns it.
/// </para>
/// </remarks>
public sealed class InspectionWorkItem : WorkItem<InspectionWorkflowState>
{
    /// <summary>
    /// Discriminator value the platform writes into
    /// <see cref="WorkItem{TState}.Workflow"/> for every inspection
    /// work item. Stable identifier — used as the partition key in
    /// the future cross-module timeline view.
    /// </summary>
    public const string WorkflowName = "inspection";

    /// <summary>
    /// Default-construct a work item with the workflow discriminator
    /// pre-populated. EF Core uses this constructor for materialisation;
    /// application code calls it for the create path and then sets
    /// <see cref="CaseId"/>, <see cref="WorkItem{TState}.IdempotencyAnchor"/>,
    /// and the initial <see cref="WorkItem{TState}.CurrentState"/> before
    /// the first <c>SaveChanges</c>.
    /// </summary>
    public InspectionWorkItem()
    {
        Workflow = WorkflowName;
    }

    /// <summary>
    /// FK to the <see cref="InspectionCase"/> this work item flows for.
    /// One work item per case in the typical path; the <c>cases</c>
    /// table side has no navigation back (kept unidirectional during
    /// the legacy <c>CaseWorkflowService</c> cutover so the case shape
    /// is unchanged for existing readers).
    /// </summary>
    public Guid CaseId { get; set; }
}
