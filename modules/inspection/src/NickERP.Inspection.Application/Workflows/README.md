# Adding a queue + consumer to the inspection pipeline

This folder is the canonical home for inspection-module queue payloads
and consumers built on top of `NickERP.Platform.Queueing`. The Sprint 14
`SplitDetectionPayload` + `SplitDetectionConsumer` pair (in this folder)
is the working reference; the state-machine producer that drives the
queue is `InspectionStateMachine` in `../StateMachines/`.

5 steps to add a new queue. All snippets reference the SplitDetection
example so you can diff against the working pair as you go.

## 1. Define a payload record

Records are the v2 convention — value-equality + structural
deserialisation for free, and the queueing platform's JSON serialiser is
configured for `JsonSerializerDefaults.Web`. Keep payloads minimal: the
queueing layer carries `WorkItemId` for you, so the payload only needs
the consumer-specific reference set.

```csharp
// Workflows/MyWorkPayload.cs
namespace NickERP.Inspection.Application.Workflows;

public sealed record MyWorkPayload(Guid CaseId, string SomeRef);
```

Reference: `SplitDetectionPayload.cs`.

## 2. Implement `IQueueConsumer<TPayload>`

One consumer per payload type. The host wraps the call and routes thrown
exceptions to `IQueueClaim<TPayload>.FailAsync` — consumers don't call
`FailAsync` directly. `ProcessAsync` must be idempotent under retry: the
queueing layer guarantees at-least-once delivery, not exactly-once.

```csharp
// Workflows/MyWorkConsumer.cs
public sealed class MyWorkConsumer : IQueueConsumer<MyWorkPayload>
{
    private readonly ILogger<MyWorkConsumer> _logger;
    public MyWorkConsumer(ILogger<MyWorkConsumer> logger) => _logger = logger;

    public Task ProcessAsync(IQueueClaim<MyWorkPayload> claim, CancellationToken ct)
    {
        _logger.LogInformation("processed CaseId={CaseId}", claim.Payload.CaseId);
        return Task.CompletedTask;
    }
}
```

Reference: `SplitDetectionConsumer.cs`.

## 3. Add a migration for `inspection.queue_<name>`

Mirror `20260506130000_Add_QueueSplitDetection.cs` — the platform's
`PostgresQueue<TPayload>` binds to `"{Schema}"."queue_{Name}"`, so the
table shape (column names, casing, defaults) and the four indexes
(unique idempotency-key, partial available-unclaimed, work-item-id
lookup, partial claimed-until) are part of the platform contract.

```csharp
migrationBuilder.Sql(@"CREATE TABLE inspection.queue_my_work ( ... );");
migrationBuilder.Sql(@"CREATE UNIQUE INDEX ux_queue_my_work_idempotency_key
    ON inspection.queue_my_work (""IdempotencyKey"");");
migrationBuilder.Sql("ALTER TABLE inspection.queue_my_work ENABLE ROW LEVEL SECURITY;");
migrationBuilder.Sql("ALTER TABLE inspection.queue_my_work FORCE ROW LEVEL SECURITY;");
migrationBuilder.Sql("CREATE POLICY tenant_isolation_queue_my_work ON ...");
```

Reference: `../../NickERP.Inspection.Database/Migrations/20260506130000_Add_QueueSplitDetection.cs`.

## 4. Wire DI in Program.cs

Two extension methods from `NickERP.Platform.Queueing`. Both are
idempotent — call them once per (queue, consumer) pair next to the
existing Sprint 14 block in `NickERP.Inspection.Web/Program.cs`.

```csharp
builder.Services.AddPostgresQueue<MyWorkPayload>(opts =>
{
    opts.Schema = "inspection";
    opts.Name = "my_work";
});
builder.Services.AddQueueConsumer<MyWorkConsumer, MyWorkPayload>();
```

Reference: the `--- Sprint 14 / B-queues ---` block in
`../../NickERP.Inspection.Web/Program.cs`.

## 5. Producer: enqueue from `WorkItemStateMachine.OnTransitionedAsync`

The canonical producer for inspection-module queues is the state machine.
Inject `ITransactionalQueue<TPayload>` into the machine; emit the row from
the transaction-aware `OnTransitionedAsync` overload so the enqueue
commits in the same transaction as the state change (the platform base
wraps both in one DB transaction).
Build the idempotency key from the work item's stable anchor + trigger +
destination state so retries collapse to one row.

```csharp
protected override async Task OnTransitionedAsync(
    DbContext db,
    InspectionWorkItem workItem, InspectionWorkflowState fromState,
    InspectionWorkflowState toState, InspectionTrigger trigger,
    string actor, string reason, string? correlationId, CancellationToken ct)
{
    if (toState != InspectionWorkflowState.SomeState) return;
    await _myWorkQueue.EnqueueAsync(db, new EnqueueRequest<MyWorkPayload>
    {
        WorkItemId = workItem.Id,
        Payload = new MyWorkPayload(workItem.CaseId, "..."),
        IdempotencyKey = IdempotencyKey.From(
            workItem.IdempotencyAnchor, trigger.ToString(), toState.ToString()),
        CorrelationId = correlationId
    }, ct).ConfigureAwait(false);
}
```

Reference: `../StateMachines/InspectionStateMachine.cs`'s
`OnTransitionedAsync` override (the split-detection enqueue on
`Open → Validated`).
