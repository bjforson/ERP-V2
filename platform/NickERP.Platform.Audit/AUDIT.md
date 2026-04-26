# NickERP.Platform.Audit

> Status: A.5 partial — DomainEvent + IEventPublisher / IEventBus contracts + IdempotencyKey helper shipped. Persistence + LISTEN/NOTIFY bus implementation in `NickERP.Platform.Audit.Database`. Notifications inbox + projector host on the backlog.
>
> See `ROADMAP.md §A.5`.

---

## What this layer does

A single, append-only stream of every state-change in the suite. Three benefits stacked into one record:

1. **Compliance audit trail.** "Who modified case X at time Y" is a single SQL query against `audit.events` — no per-module log scraping.
2. **Cross-app integration via events.** A module emitting an event lets other modules react (e.g. `inspection.case_reviewed` → AR module starts an invoice flow) without point-to-point HTTP.
3. **Idempotency.** Events carry an `IdempotencyKey`; replays / retries don't double-apply.

All three are powered by the same `audit.events` table. Modules emit; the platform persists + dispatches.

## The core record

```csharp
DomainEvent(
    Guid EventId,
    long TenantId,
    Guid? ActorUserId,
    string? CorrelationId,
    string EventType,             // "nickerp.identity.user_created"
    string EntityType,            // "IdentityUser"
    string EntityId,              // serialised primary key
    JsonElement Payload,          // diff or full row, schema per EventType
    DateTimeOffset OccurredAt,    // when the change happened
    DateTimeOffset IngestedAt,    // when audit row was written
    string IdempotencyKey,
    string? PrevEventHash);       // optional tamper-evident chain
```

Construct via `DomainEvent.Create(...)` or directly. EventId is empty until the persistence layer assigns it.

## Module-facing API

```csharp
public interface IEventPublisher
{
    Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default);
}
```

Modules call this from application services after a state change:

```csharp
var evt = DomainEvent.Create(
    tenantId: tenant.TenantId,
    actorUserId: currentUser.Id,
    correlationId: Activity.Current?.RootId,
    eventType: "nickerp.inspection.case_reviewed",
    entityType: "InspectionCase",
    entityId: caseId.ToString(),
    payload: JsonSerializer.SerializeToElement(new { caseId, verdict }),
    idempotencyKey: IdempotencyKey.ForEntityChange(
        tenantId, "nickerp.inspection.case_reviewed", "InspectionCase",
        caseId.ToString(), DateTimeOffset.UtcNow));
await _publisher.PublishAsync(evt);
```

Handlers (cross-app integration) wire up via `IEventBus.Subscribe(channel, ...)` — the channel name conventionally matches the `EventType` prefix (e.g. subscribe to `nickerp.inspection.*` to receive every inspection event).

## Idempotency

Every event MUST carry a deterministic key. `IdempotencyKey.ForEntityChange(...)` is the standard helper — it rounds the timestamp to the second so two near-simultaneous emitters of the same logical event collide on a single key. Manual `IdempotencyKey.From(parts)` is fine when the entity-change pattern doesn't fit.

The persistence layer treats duplicate keys (within a tenant) as no-ops; `PublishAsync` returns the original event row, not a fresh insert.

## Append-only enforcement

`audit.events` is built append-only by:

1. The schema has no UPDATE or DELETE codepath in the EF model.
2. Ops `REVOKE UPDATE, DELETE ON audit.events FROM <appRole>` at deploy time.
3. The optional `PrevEventHash` field can be filled in (per-EntityId chain) for tamper-evident audit when compliance demands it.

(2) is documented but not part of the migration — apply it manually for each environment per `AUDIT.md` §"Append-only enforcement at the role level".

## Open contract questions

- [ ] **Notifications inbox** — derived projection from `audit.events` filtered to a per-user actionable subset. Schema sketched but not yet shipped.
- [ ] **Cross-tenant subscribers** — today the bus is tenant-scoped via the publisher. Some operational subscribers (e.g. ops alerting) want every tenant. Add a `TenantId == 0L` "system" channel? Defer.
- [ ] **Out-of-process delivery** — `IEventBus` contract is in-process semantics. Postgres LISTEN/NOTIFY gets us cross-process within a single host machine. Multi-host deployments would need Redis pub/sub or similar. Phase 5 problem.

## Out of scope

- Replay / event sourcing. `audit.events` is a side-record of state changes, not a source-of-truth that gets replayed to rebuild aggregates. Modules keep their own write-side state.
- Event versioning / migration. We're at v0; if an `EventType` schema needs to change, version it (`nickerp.x.user_created.v2`) and have consumers handle both for a deprecation window.

## Related

- `IDENTITY.md` — `ActorUserId` resolves to canonical users from the Identity layer.
- `TENANCY.md` — every event carries `TenantId`.
- `ROADMAP.md §A.5` — task list and acceptance criteria.
