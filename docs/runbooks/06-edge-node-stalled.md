# Runbook 06 — Edge node stalled

## 1. Symptom

An edge node's local `edge_outbox` queue depth is growing and the
events aren't draining to the central server. Visible via:

- The edge's `/edge/healthz` endpoint shows `queueDepth` rising tick
  on tick.
- Operator dashboards / alerting that scrape `queueDepth` flag the
  edge.
- Server-side `audit.edge_node_replay_log` shows no rows for this
  edge in the last hour.
- (Optionally) `LastReplayError` columns on the edge's
  `edge_outbox` rows hold the most recent server-side rejection.

## 2. Severity

| Failure shape | Severity | Response |
|---|---|---|
| Queue depth growing slowly (< 1k events, link known to be flaky) | P3 | Log; verify the edge eventually recovers when link returns. |
| Queue depth > 5k OR last successful replay > 1 h ago OR `LastReplayError` populated across all rows | P2 | Page on-call; expect a config / auth / authorization regression. |
| Queue depth at or beyond the SQLite file's free-disk threshold | P1 | Page now — disk-full will brick the edge box once the buffer can't append. |

## 3. Quick triage (5 minutes)

Pick the path based on what `/edge/healthz` reports plus a glance at
the most recent error column.

```
GET https://<edge-host>:<edge-port>/edge/healthz
```

```
{
  "edgeNodeId": "edge-tema-1",
  "queueDepth": 217,
  "lastSuccessfulReplayAt": "2026-04-29T09:12:18.000Z"
}
```

If `lastSuccessfulReplayAt` is null and the edge has been up for
more than one replay interval, the edge has never successfully
reached the server. Skip to §5.1.

If `lastSuccessfulReplayAt` is recent but the queue is still
growing, the edge is being authoritatively rejected by the server —
skip to §5.2.

If `lastSuccessfulReplayAt` is old but not null, the link to the
server has gone away after past success — skip to §5.3.

## 4. Diagnostic commands

### 4.1 Inspect the local SQLite buffer

```bash
# On the edge box.
sqlite3 /path/to/edge-outbox.db <<'SQL'
SELECT
  COUNT(*)        AS total,
  COUNT(ReplayedAt)              AS replayed,
  COUNT(*) FILTER (WHERE ReplayedAt IS NULL) AS pending,
  MAX(EdgeTimestamp) FILTER (WHERE ReplayedAt IS NULL)  AS oldest_pending,
  MAX(ReplayAttempts) FILTER (WHERE ReplayedAt IS NULL) AS worst_attempts
FROM edge_outbox;
SELECT id, ReplayAttempts, LastReplayError
FROM edge_outbox
WHERE ReplayedAt IS NULL
ORDER BY id DESC LIMIT 10;
SQL
```

### 4.2 Check the server's view of this edge

```bash
# As `nscim_app` against the platform DB. The edge_node_authorizations
# table is suite-wide reference data (no RLS), so a plain SELECT works.
psql -U nscim_app -d nickerp_platform <<'SQL'
SELECT * FROM audit.edge_node_authorizations
WHERE "EdgeNodeId" = 'edge-tema-1';

SELECT *
FROM audit.edge_node_replay_log
WHERE "EdgeNodeId" = 'edge-tema-1'
ORDER BY "ReplayedAt" DESC
LIMIT 10;
SQL
```

### 4.3 Server reachability from the edge

```bash
# From the edge box, against the configured Server:Url.
curl -sf "https://<server>/healthz/ready" || echo "UNREACHABLE"
# Edge sends its token on every replay POST. Manually probe with the
# edge's configured token (DO NOT log the token):
EDGE_TOKEN="$(read-secret EDGE_NODE_TOKEN)"  # however the box stores it
curl -i -X POST "https://<server>/api/edge/replay" \
  -H "Content-Type: application/json" \
  -H "X-Edge-Token: $EDGE_TOKEN" \
  -d '{"edgeNodeId":"edge-tema-1","events":[]}'
# Expect HTTP 200 + an empty results array. 401 means token mismatch.
```

## 5. Resolution

### 5.1 Edge has never reached the server

Most common in a fresh deploy. Check, in order:

1. **`Server:Url`** — does the configured value resolve to the
   server's actual address from the edge box? Check DNS, firewall.
2. **`X-Edge-Token`** — does the edge's `EdgeNode:Token` match the
   server's `EdgeNode:SharedSecret`? They MUST be byte-for-byte
   equal (constant-time compared).
3. **`/healthz/ready`** — the edge probes this every tick. If the
   server's ready probe is 503 (e.g. DB regression), the edge will
   never advance past §3a even though POSTs would actually work.
   See [`02-secret-rotation.md`](02-secret-rotation.md) for DB
   regression resolution.

After fixing, restart the edge worker (or just wait for the next
tick — the worker doesn't cache reachability state across ticks).

### 5.2 Server returning per-event 4xx (`LastReplayError` populated)

Look at the error message to pick the path:

| `LastReplayError` text | Cause | Fix |
|---|---|---|
| `tenant <N> not authorized for edge <id>` | No row in `audit.edge_node_authorizations` for this (edge, tenant) pair. | Insert the row as superuser (operator action; the host can't mutate this table). The edge keeps retrying — pending rows drain on the next tick. |
| `unsupported eventTypeHint '<x>'` | Edge captured a hint v0 doesn't know. | Either redeploy the edge with v0-shaped captures (audit-event-only), or bump the server to a version that handles the hint. v0 supports only `audit.event.replay`. |
| `payload missing required fields: ...` | Edge captured a malformed payload. | Investigate the capturing adapter; a payload without `eventType`/`entityType`/`entityId` is a bug at the call site. |
| `edge timestamp ... is more than 60s in the future ...` | Edge clock is wrong. | Fix NTP on the edge. The captured rows aren't lost — fix the clock and they replay successfully on the next tick (the edge timestamp survives unchanged through the buffer). |

For all of the above, the edge keeps retrying — events are never
dropped silently. Each tick re-attempts the queue head; once the
underlying issue is fixed, the queue drains.

### 5.3 Edge had successful replays, then stopped

Treat as a transient failure that's gotten stuck. Run §4.3 from the
edge to confirm the link is back; if it is, just wait one tick (or
restart the edge worker for a bias toward fast recovery).

If the link is genuinely down for an extended period (hours+),
verify on the server side that the audit DB and `/healthz/ready` are
green. A server-side 503-loop will silently keep edges from
draining; that's a server problem, not an edge problem (see
[`03-prerender-stalled.md`](03-prerender-stalled.md) for a related
shape).

### 5.4 SQLite file approaching disk limit

Two recovery paths, neither of them weakening posture:

1. **Drain to the server** — the safest path. If the link is
   reachable, the worker drains automatically; if it isn't, fix
   the link, drain, then prune.
2. **Manual prune of replayed rows.** Replayed rows are the audit
   trail of what the edge has shipped — keep them as long as you
   can — but they're not load-bearing for replay. Pruning them is
   safe:

```bash
sqlite3 /path/to/edge-outbox.db <<'SQL'
DELETE FROM edge_outbox WHERE ReplayedAt IS NOT NULL;
VACUUM;
SQL
```

Do NOT prune unreplayed rows. They are events that the central
server has not yet seen; deleting them is data loss.

## 6. Verification

```bash
# Edge side:
curl -s "https://<edge-host>:<edge-port>/edge/healthz" | jq .
# queueDepth should be trending down; lastSuccessfulReplayAt should
# be recent (within 2x ReplayIntervalSeconds).

# Server side:
psql -U nscim_app -d nickerp_platform -c \
  "SELECT \"ReplayedAt\", \"OkCount\", \"FailedCount\"
   FROM audit.edge_node_replay_log
   WHERE \"EdgeNodeId\" = 'edge-tema-1'
   ORDER BY \"ReplayedAt\" DESC LIMIT 5;"
```

## 7. Aftermath

- **If the cause was an unauthorized tenant**, audit
  `audit.edge_node_authorizations` for the rest of your edges to
  confirm there isn't a class regression (the seed step was missed
  for several edges).
- **If the cause was a clock skew**, double-check that the edge's
  NTP service is enabled to start at boot — a single fix without a
  service-enable will recur on next reboot.
- **If the cause was a 4xx that turned out to be a v0 limitation**
  (e.g. an event type the server doesn't yet handle), file a
  follow-up to extend the dispatcher, then redeploy. Until the
  follow-up lands, the edge will keep failing those events; consider
  whether to deactivate the capturing adapter to stop buffering.
- **If the SQLite buffer needed pruning**, plan a follow-up to
  automate retention (cron a `DELETE WHERE ReplayedAt < now() -
  retention_window` against the edge's SQLite). v0 leaves this
  manual.

## 8. References

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) §14 — edge node design.
- [`../system-context-audit-register.md`](../system-context-audit-register.md)
  — `EdgeReplayEndpoint.HandleAsync` is registered as a
  `SetSystemContext()` caller.
- [`../../apps/edge-node/README.md`](../../apps/edge-node/README.md)
  — operational doc for deploying an edge node (config keys,
  service-install steps).
- [`../../apps/edge-node/NickERP.EdgeNode/Program.cs`](../../apps/edge-node/NickERP.EdgeNode/Program.cs)
  — startup wiring of the worker, the buffer, and the healthz
  endpoint.
- [`../../modules/inspection/src/NickERP.Inspection.Web/Endpoints/EdgeReplayEndpoint.cs`](../../modules/inspection/src/NickERP.Inspection.Web/Endpoints/EdgeReplayEndpoint.cs)
  — server-side replay handler; useful when triaging per-entry
  errors.
- [`../../tools/edge-sqlite/edge-outbox-schema.sql`](../../tools/edge-sqlite/edge-outbox-schema.sql)
  — the SQLite DDL the edge initialises from on first boot.
