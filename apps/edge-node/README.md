# NickERP Edge Node

Sprint 11 / P2 — the edge-node host. Runs on a small box at a
physical location where the central NickERP server is intermittently
reachable: port-of-entry inspection lanes, remote NickFinance
branches with flaky links, scanner-attached field nodes.

The edge captures events into a local SQLite buffer (`edge_outbox`)
and a background worker drains them to the central server when it
becomes reachable. Edges cannot mutate or delete server-side state —
only append. See [`docs/ARCHITECTURE.md` §14](../../docs/ARCHITECTURE.md)
for the architectural posture.

## Prerequisites

- .NET 10 runtime (matches the rest of NickERP v2).
- Network reachability to the central server's `/healthz/ready` and
  `/api/edge/replay`. The edge does NOT need a public IP — it makes
  outbound HTTPS calls.
- A pre-shared `EdgeNode:Token` matching the server's
  `EdgeNode:SharedSecret`. v0 — single shared secret across edges.
  See [§Hardening](#hardening) below for the proper-edge-auth TODO.
- Authorization rows seeded on the server: an admin must INSERT one
  row into `audit.edge_node_authorizations` per `(EdgeNodeId,
  TenantId)` pair this edge is allowed to ship events for. v0 has
  no admin UI for this — operator runs the SQL directly under the
  `postgres` superuser.

## Configuration (`appsettings.json`)

```json
{
  "Server": {
    "Url": "https://nickerp.example.com"
  },
  "EdgeNode": {
    "Id": "edge-tema-1",
    "Token": "<shared-secret>",
    "ReplayIntervalSeconds": 30,
    "MaxBatchSize": 50,
    "EnsureBufferCreated": false
  },
  "ConnectionStrings": {
    "EdgeBuffer": "Data Source=C:\\NickERP\\edge\\edge-outbox.db"
  }
}
```

| Key | Purpose | Default |
|---|---|---|
| `Server:Url` | Central NickERP base URL. Edge probes `<url>/healthz/ready` + posts to `<url>/api/edge/replay`. | (no default — required) |
| `EdgeNode:Id` | Stable identifier for this edge box. Server uses it for authorization. MUST be unique across the suite. | (no default — required) |
| `EdgeNode:Token` | Shared secret presented in the `X-Edge-Token` header on every replay POST. | (empty) |
| `EdgeNode:ReplayIntervalSeconds` | How often the worker probes + drains. | 30 |
| `EdgeNode:MaxBatchSize` | Max events per replay POST. | 50 |
| `EdgeNode:EnsureBufferCreated` | Run `EnsureCreated()` on the SQLite file at startup. Default true in Development, false otherwise. Use the DDL script in production. | env-default |
| `ConnectionStrings:EdgeBuffer` | SQLite file path. Default puts it next to the binary — override for production. | `Data Source=edge-outbox.db` |

### First-run schema seeding

In **Development**, the host calls `EnsureCreated()` if
`EdgeNode:EnsureBufferCreated` is true (default). In **Production**,
seed the file once before the first boot using the DDL script:

```bash
sqlite3 /path/to/edge-outbox.db < tools/edge-sqlite/edge-outbox-schema.sql
```

The script is idempotent (`CREATE TABLE IF NOT EXISTS`), so re-
running on an already-seeded file is a no-op.

## Authorize the edge for tenants

Every (edge, tenant) pair the edge is allowed to ship events for
needs a row in `audit.edge_node_authorizations`:

```sql
-- As `postgres` (nscim_app has SELECT only on this table).
INSERT INTO audit.edge_node_authorizations
  ("EdgeNodeId", "TenantId", "AuthorizedAt", "AuthorizedByUserId")
VALUES
  ('edge-tema-1', 17, now(), '<your-user-id>');
```

The endpoint reads the rows under system context on every replay
batch. Removing a row deauthorizes future replays for that pair —
already-replayed audit rows stay; in-flight batches reject with a
per-entry error.

## Run the worker

### Locally (Development)

```bash
cd apps/edge-node/NickERP.EdgeNode
dotnet run
# Health: http://localhost:<auto-port>/edge/healthz
```

### As a Windows service (Production)

This sprint does not yet ship NSSM bindings — the [`Deploy.ps1`
follow-up](../../docs/product-calls-2026-04-29.md#31-fu-deploy--deployps1-for-erp-v2)
will add edge-node support when it lands. For now, install via
NSSM by hand using the same pattern as the other v2 services:

```powershell
nssm install NickERP_EdgeNode "C:\NickERP\edge\NickERP.EdgeNode.exe"
nssm set NickERP_EdgeNode AppDirectory "C:\NickERP\edge"
nssm set NickERP_EdgeNode ObjectName LocalSystem
nssm set NickERP_EdgeNode AppEnvironmentExtra `
    EDGENODE_TOKEN=<shared-secret>
nssm start NickERP_EdgeNode
```

## Observability

The edge exposes one operational endpoint:

```
GET /edge/healthz
```

Body:

```json
{
  "edgeNodeId": "edge-tema-1",
  "queueDepth": 12,
  "lastSuccessfulReplayAt": "2026-04-29T11:30:00.0000000+00:00"
}
```

- `queueDepth` is the number of unreplayed rows in the local
  buffer. Tick-on-tick increase + no `lastSuccessfulReplayAt`
  movement is the signal that the edge is stalled — see
  [`docs/runbooks/06-edge-node-stalled.md`](../../docs/runbooks/06-edge-node-stalled.md).

## Hardening

### v0 limitations (deliberate, addressed in follow-ups)

1. **Shared-secret auth.** All edges present the same
   `X-Edge-Token`. Rotating invalidates every edge until they're
   re-deployed with the new value. Per-edge mTLS or per-edge JWTs
   is the proper fix; landed in a future sprint.
2. **Network ACL is the load-bearing perimeter.** The shared-secret
   check is necessary but not sufficient — keep the `/api/edge/replay`
   endpoint behind a firewall rule that limits ingress to known
   edge addresses.
3. **No admin UI for `audit.edge_node_authorizations`.** Operators
   seed via psql under `postgres`. v0 keeps the surface small.
4. **One event type.** v0 supports only `audit.event.replay`. The
   capturing adapter must shape its payload to a `DomainEvent`
   (`eventType`, `entityType`, `entityId`, optional
   `actorUserId`, `correlationId`). Other event types (scan-
   captured, voucher-disbursed) are a follow-up sprint.
5. **No retention pruning.** Replayed rows stay in the SQLite file
   forever; for long-running edges, plan a cron job that runs
   `DELETE FROM edge_outbox WHERE ReplayedAt < now() - retention`
   then `VACUUM`. The runbook calls this out.

### v0 invariants (do NOT relax without confirmation)

- **Append-only.** The edge writes audit-shaped events; the server
  writes audit rows. No UPDATEs, no DELETEs cross the seam.
- **FIFO per edge.** The worker drains by ascending `Id`. Out-of-
  order replay would break the captured causality.
- **Edge timestamps preserved.** The server uses
  `EdgeTimestamp` as the audit row's `OccurredAt`. Don't substitute
  the server clock — it would lie about when the event happened.
- **Future-dated edge timestamps rejected.** The server tolerates
  60s of clock skew but rejects anything beyond.

## See also

- [`docs/ARCHITECTURE.md` §14](../../docs/ARCHITECTURE.md) — design.
- [`docs/runbooks/06-edge-node-stalled.md`](../../docs/runbooks/06-edge-node-stalled.md)
  — operational playbook when this thing goes wrong.
- [`docs/system-context-audit-register.md`](../../docs/system-context-audit-register.md)
  — `EdgeReplayEndpoint.HandleAsync` is registered as a
  `SetSystemContext()` caller.
- [`tools/edge-sqlite/edge-outbox-schema.sql`](../../tools/edge-sqlite/edge-outbox-schema.sql)
  — the DDL the edge initialises against on first boot.
