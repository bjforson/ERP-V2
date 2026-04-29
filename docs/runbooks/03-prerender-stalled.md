# Runbook 03 — `PreRenderWorker` not draining

> **Scope.** The image pipeline's `PreRenderWorker` is supposed to
> drain unrendered `ScanArtifact` rows into `ScanRenderArtifact` rows
> on a 5-second poll cycle (3 s in dev). When it stalls, the analyst
> UI shows the "preview still rendering" state forever. This runbook
> covers the four root causes we've seen plus the verification
> commands.
>
> **Sister docs:** [`04-plugin-load-failure.md`](04-plugin-load-failure.md)
> if `PluginRegistryHealthCheck` is the *cause*; the worker silently
> drains zero artifacts when no `IScannerAdapter`-backed plugins are
> loaded because nothing produces `ScanArtifact` rows.

---

## 1. Symptom

Any of:

- **`/healthz/ready` reports `Healthy` but `imaging-storage` returns
  a degraded message.** Worker is alive but can't write derivatives.
- **Analyst UI** sits on the "preview still rendering" placeholder
  past the usual ~1 s wall-clock. Refresh doesn't help.
- **Log line** `PreRenderWorker cycle failed; backing off` — emitted
  on a caught exception inside `ExecuteAsync`. One occurrence is
  noise; sustained occurrences are an incident.
- **Telemetry** — `nickerp.inspection.image.serve_ms` counters
  flat-line for `kind=thumbnail` `status=404`, while ingestion is
  still running. Means new artifacts are arriving but the worker is
  not catching up.
- **`scan_render_attempts`** rows piling up with `AttemptCount`
  climbing toward `ImagingOptions.MaxRenderAttempts` (default 5) —
  the worker is *trying* but failing repeatedly.

## 2. Severity

| Pattern | Severity | Response window |
|---|---|---|
| Single dropped artifact, no backlog | P3 | log, ignore |
| Backlog growing for one tenant only | P2 | inside 4 h |
| Backlog growing for all tenants | P1 | inside 1 h |
| `imaging-storage` Unhealthy on `/healthz/ready` | P1 | inside 30 min |
| `MaxRenderAttempts` exhausted on >10 artifacts | P2 | inside 4 h |

A stalled worker doesn't break ingestion — `IScannerAdapter` rows
land regardless. It breaks the analyst UI, silently. A long stall
means a backlog you have to drain after the fix.

## 3. Quick triage (60 seconds)

```bash
# Is the host even up?
curl -s http://127.0.0.1:5410/healthz/ready | jq .
```

If `imaging-storage` is `Unhealthy`, jump to §5.4 (disk / permissions).
If `postgres-inspection` is `Unhealthy`, jump to §5.1 (DB pathway).
Otherwise the worker is reachable — go on to §4.

```bash
# Is there actually a backlog?
psql -U postgres -d nickerp_inspection -c \
  'SELECT COUNT(*) AS unrendered
   FROM inspection.scan_artifacts a
   WHERE NOT EXISTS (
     SELECT 1 FROM inspection.scan_render_artifacts r
     WHERE r."ScanArtifactId" = a."Id" AND r."Kind" = '"'"'thumbnail'"'"'
   );'
```

`0` means the worker is current; the analyst-UI symptom is something
else (browser cache, ETag bug). A growing positive number is the
real stall.

```bash
# Is the worker still alive at all?
# Tail Seq for "PreRenderWorker started" (start-of-run) and "rendered
# N derivative(s)" (drain progress). The worker logs at LogLevel
# Information on start, Debug per drained batch.
```

## 4. Diagnostic commands

### 4.1 Map the backlog

```bash
# How many artifacts are missing each kind?
psql -U postgres -d nickerp_inspection -c \
  'SELECT
     SUM(CASE WHEN NOT thumb THEN 1 ELSE 0 END) AS missing_thumbnail,
     SUM(CASE WHEN NOT prev  THEN 1 ELSE 0 END) AS missing_preview,
     SUM(CASE WHEN NOT thumb AND NOT prev THEN 1 ELSE 0 END) AS missing_both
   FROM (
     SELECT
       EXISTS (SELECT 1 FROM inspection.scan_render_artifacts r
               WHERE r."ScanArtifactId" = a."Id" AND r."Kind" = '"'"'thumbnail'"'"') AS thumb,
       EXISTS (SELECT 1 FROM inspection.scan_render_artifacts r
               WHERE r."ScanArtifactId" = a."Id" AND r."Kind" = '"'"'preview'"'"')   AS prev
     FROM inspection.scan_artifacts a
   ) x;'
```

### 4.2 Map the per-tenant slice

```bash
# Backlog by tenant — is one tenant stuck or all of them?
psql -U postgres -d nickerp_inspection -c \
  'SELECT a."TenantId", COUNT(*) AS unrendered
   FROM inspection.scan_artifacts a
   WHERE NOT EXISTS (
     SELECT 1 FROM inspection.scan_render_artifacts r
     WHERE r."ScanArtifactId" = a."Id" AND r."Kind" = '"'"'thumbnail'"'"'
   )
   GROUP BY a."TenantId"
   ORDER BY unrendered DESC;'
```

If only one tenant has a backlog, it's almost certainly that tenant's
RLS context — see §5.3. If every tenant is stuck, the worker is dead
or the host can't write to disk — §5.2 / §5.4.

### 4.3 Read the attempt-count history

```bash
# Recent attempts — what's the worker tripping on?
psql -U postgres -d nickerp_inspection -c \
  'SELECT a."Id", a."Kind", a."AttemptCount",
          a."LastAttemptAt", a."PermanentlyFailedAt",
          LEFT(a."LastError", 200) AS error_preview
   FROM inspection.scan_render_attempts a
   ORDER BY a."LastAttemptAt" DESC
   LIMIT 20;'
```

Patterns:

- **`source bytes missing: ...`** → §5.4 (the source blob is gone but
  the artifact row is still there; ImageStore eviction race or manual
  cleanup).
- **EF / RLS-related exceptions** → §5.3.
- **ImageSharp decode failures** (`Image cannot be loaded`) → almost
  always a corrupt / partial source file from ingestion; treat as a
  poison message, let it permanently-fail, investigate the upstream
  ingestion separately.
- **`Disk full` / `IOException`** → §5.4.

### 4.4 Confirm worker liveness

The worker registers as a `BackgroundService` via
`AddNickErpImaging` in `Program.cs`. There is no host-level "worker
status" endpoint as of Sprint 7 — liveness is inferred from log
volume. Tail Seq for the host filtered to the `PreRenderWorker`
SourceContext. The "alive" signature is a `Debug` event every
`WorkerPollIntervalSeconds` (5 s prod, 3 s dev) — quiet periods longer
than 30 s mean the worker is wedged.

If the host as a whole has had no log events for 30+ s, the host
process itself is wedged — different problem, restart the host.

### 4.5 Reproduce a render manually

```bash
# Pick one stuck artifact — what does its source blob look like?
psql -U postgres -d nickerp_inspection -c \
  'SELECT a."Id", a."ContentHash", a."MimeType",
          (SELECT COUNT(*) FROM inspection.scan_render_artifacts r
           WHERE r."ScanArtifactId" = a."Id") AS rendered_kinds,
          (SELECT COUNT(*) FROM inspection.scan_render_attempts t
           WHERE t."ScanArtifactId" = a."Id" AND t."PermanentlyFailedAt" IS NOT NULL) AS dead_kinds
   FROM inspection.scan_artifacts a
   WHERE a."CreatedAt" > now() - interval '"'"'1 day'"'"'
   ORDER BY a."CreatedAt" ASC
   LIMIT 5;'

# Confirm the source blob exists on disk for that hash.
HASH=<paste-from-above>
EXT=<png|jpg|tiff>
ls -la "C:/Shared/ERP V2/.imaging-store/source/${HASH:0:2}/$HASH.$EXT"
```

The path layout is content-addressed: `source/{hash[0..2]}/{hash}{ext}`.
A missing file there is the cause for `source bytes missing` in
§4.3.

## 5. Resolution paths

### 5.1 Postgres path is broken

Symptom: `postgres-inspection` `Unhealthy` on `/healthz/ready`.
Worker can't open a DbContext, every cycle fails inside `DrainOnceAsync`.

```bash
# Restore Postgres connectivity first (this isn't a worker-specific bug).
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_inspection -c "SELECT 1;"
```

If this fails: see [`02-secret-rotation.md`](02-secret-rotation.md) §5
(password regression) or check Postgres up at all.

After Postgres is back: the worker recovers on its next cycle —
no manual restart needed unless the host process is wedged. Watch
§4.1's count trend down for 1–2 minutes.

### 5.2 Worker is wedged but the host is up

Symptom: `/healthz/ready` Healthy, no `PreRenderWorker` log events
in 60+ s, backlog growing.

```bash
# Restart the host — the only way to bring the BackgroundService
# back without code change. See 01-deploy.md §5.4 for the stop/start
# mechanics.
```

Before the restart, snapshot `scan_render_attempts` so you can tell
the difference between "wedge cleared" and "still wedged":

```bash
psql -U postgres -d nickerp_inspection -c \
  'SELECT MAX("LastAttemptAt") FROM inspection.scan_render_attempts;'
```

After restart, this timestamp should advance within
`WorkerPollIntervalSeconds + 1` s. If it doesn't, the worker is
dying on startup — read the host's first 30 s of logs.

### 5.3 Tenancy regression — RLS hiding rows

Symptom: backlog confined to one or a small set of tenants; the
worker's drain query returns 0 for those tenants while their
`scan_artifacts` rows clearly exist (visible to `postgres`).

The worker walks `tenancy.tenants` and calls `ITenantContext.SetTenant`
per active tenant before each drain (see `PreRenderWorker.DrainOnceAsync`).
If `app.tenant_id` isn't being pushed to Postgres on the connection,
RLS hides every row from the worker's view. Symptoms:

```bash
# Same query the worker uses — but as postgres (BYPASSRLS) it should
# see the rows the worker doesn't.
psql -U postgres -d nickerp_inspection -c \
  'SELECT a."TenantId", COUNT(*)
   FROM inspection.scan_artifacts a
   WHERE NOT EXISTS (
     SELECT 1 FROM inspection.scan_render_artifacts r
     WHERE r."ScanArtifactId" = a."Id"
   )
   GROUP BY a."TenantId";'

# Now repeat as nscim_app with app.tenant_id explicitly set:
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_inspection -c \
  "SET app.tenant_id = '1';
   SELECT COUNT(*)
   FROM inspection.scan_artifacts
   WHERE NOT EXISTS (
     SELECT 1 FROM inspection.scan_render_artifacts r
     WHERE r.\"ScanArtifactId\" = inspection.scan_artifacts.\"Id\"
   );"
```

If the `nscim_app` query with `SET app.tenant_id` returns the same
number as the `postgres` query, RLS is wired correctly and the worker
should see the rows. If `nscim_app` returns less, RLS is doing its
job — the issue is the worker isn't `SET`-ing the tenant. Recover by:

1. Restart the host (§5.2). The worker re-enumerates active tenants
   on every cycle; a restart recovers from any in-memory state
   corruption.
2. If still broken, confirm the active tenant exists in
   `tenancy.tenants`:
   ```bash
   psql -U postgres -d nickerp_platform -c \
     'SELECT "Id", "Name", "IsActive" FROM tenancy.tenants ORDER BY "Id";'
   ```
3. If the tenant has `IsActive = false`, the worker skips it (by
   design — the brief drain query in `PreRenderWorker` filters on
   `IsActive`). Either reactivate the tenant or accept the backlog.

### 5.4 Disk full / `imaging-storage` Unhealthy

Symptom: `imaging-storage` Unhealthy in `/healthz/ready`, or
`scan_render_attempts.LastError` showing `IOException` / `Disk full` /
`UnauthorizedAccessException`.

```bash
# What does the StorageRoot look like?
df -h "C:/Shared/ERP V2/.imaging-store" 2>&1 || \
  powershell.exe -Command \
    "Get-PSDrive C | Format-List Name, Used, Free"

# How big is the cache?
du -sh "C:/Shared/ERP V2/.imaging-store/source"  2>&1
du -sh "C:/Shared/ERP V2/.imaging-store/render"  2>&1
```

Resolution paths:

- **Disk genuinely full.** Free space; do not delete from
  `source/` or `render/` directly — the `SourceJanitorWorker` is the
  only thing that should evict source blobs (it knows which are still
  referenced). Render derivatives can be deleted safely; the
  PreRenderWorker will re-render them on the next cycle. But disk
  full is more often a host-level problem (logs, dumps, other apps);
  free space outside the imaging tree first.
- **Permissions.** `imaging-storage` Healthy probe writes a 0-byte
  `.healthcheck` sentinel — failure means the host process can't
  write under `StorageRoot`. Check the service account; check
  ACLs on `C:\Shared\ERP V2\.imaging-store`.
- **`SourceRetentionDays` too short for a long-running case.** If
  the `SourceJanitorWorker` evicted the source blob but the case is
  still open, the artifact row is there with no source bytes — the
  PreRender failure is permanent. Check
  `ImagingOptions.SourceRetentionDays` (default 30); if a case
  legitimately stays open longer, that's a config bug, not an
  incident — file a follow-up.

### 5.5 Poison messages — `MaxRenderAttempts` exhausted

Symptom: `scan_render_attempts.PermanentlyFailedAt` populated for
multiple artifacts; backlog count includes "permanently dead" rows
that the worker has stopped retrying.

```bash
# How many are dead?
psql -U postgres -d nickerp_inspection -c \
  'SELECT COUNT(*) AS dead_attempts
   FROM inspection.scan_render_attempts
   WHERE "PermanentlyFailedAt" IS NOT NULL;'
```

Resolution:

1. **Investigate the cause** before retrying. The `LastError` field
   (truncated to 1900 chars in the schema) is the lead. Common
   patterns: corrupt source bytes, MIME mismatch, a single bad scan
   that should have been rejected at ingest.
2. **If the cause is fixed** (e.g. the upstream adapter was patched
   and the next cycle would succeed), re-arm the affected rows:
   ```sql
   -- Re-arm one (artifact, kind) pair for retry. The worker will
   -- pick it back up on the next cycle.
   PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
     '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
     -U nscim_app -d nickerp_inspection -c \
     "SET app.tenant_id = '<tenant-id>';
      DELETE FROM inspection.scan_render_attempts
      WHERE \"ScanArtifactId\" = '<artifact-id>'
        AND \"PermanentlyFailedAt\" IS NOT NULL;"
   ```
   The worker treats deleted rows as fresh — `AttemptCount` starts
   at 0 again. **Do not re-arm without investigating** — you'll spam
   the logs and chew CPU on the same poison message.
3. **If the cause is unfixable** (the source bytes are genuinely
   gone or unreadable), leave the rows permanently-failed. The
   analyst UI shows a "preview unavailable" state; ingestion can be
   replayed for that artifact if it matters.

## 6. Verification

After any §5 path:

1. `/healthz/ready` shows all five checks `Healthy`.
2. `nickerp.inspection.preRender.render_ms` (the histogram in
   `NickErpActivity.PreRenderRenderMs`) starts emitting samples
   again — visible in your metrics scrape, or via the Inspection
   Web admin `/perf` page.
3. The §4.1 unrendered count is **trending down**. After a stall
   that lasted N minutes you'll have a backlog of roughly
   `N * ingest_rate`; confirm it drains within
   `backlog / WorkerBatchSize / WorkerPollIntervalSeconds` seconds.
4. Pick one of the previously-stuck artifacts and confirm both
   `thumbnail` and `preview` rows now exist in `scan_render_artifacts`:
   ```bash
   psql -U postgres -d nickerp_inspection -c \
     'SELECT "Kind", "WidthPx", "HeightPx", "RenderedAt"
      FROM inspection.scan_render_artifacts
      WHERE "ScanArtifactId" = '"'"'<artifact-id>'"'"'
      ORDER BY "Kind";'
   ```
5. Hit the image endpoint:
   ```bash
   curl -s -o /tmp/thumb.png -w "%{http_code} %{size_download}\n" \
     -H "X-Dev-User: dev@nickscan.com" \
     "http://127.0.0.1:5410/api/images/<artifact-id>/thumbnail"
   ```
   `200` and a non-zero byte count means the full pipeline serves.

## 7. Aftermath

### 7.1 Postmortem template

```
## PreRender stall: <YYYY-MM-DD HH:MM>
- Detection: alert | manual triage | analyst report
- Root cause: postgres | wedged-worker | tenancy-rls | disk-full | poison-message
- Backlog peak (unrendered count): <N>
- Drain time after fix (clock-time to backlog=0): <minutes>
- Was a host restart required? <yes / no>
- Was any data lost? <permanently-failed-rows: count>
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 7.2 Who to notify

Single-engineer system today: capture in `CHANGELOG.md` and the
relevant ROADMAP / sprint tracking file. If the cause was a code bug
in the worker itself, this needs a fix in the next deploy — file
the issue.

## 8. References

- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) §7.7 — image pipeline
  design (thumbs ≤ 50 ms p95, previews ≤ 80 ms p95, content-addressed
  storage layout).
- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) §7.7.1 — cross-tenant
  blob collision posture; relevant if the §5.4 disk path is racing
  the `SourceJanitorWorker`.
- `modules/inspection/src/NickERP.Inspection.Imaging/PreRenderWorker.cs`
  — the worker itself; read it before declaring a "deep" bug.
- `modules/inspection/src/NickERP.Inspection.Imaging/ImagingOptions.cs`
  — the config knobs (`MaxRenderAttempts`, `WorkerPollIntervalSeconds`,
  `WorkerBatchSize`, `SourceRetentionDays`).
- [`04-plugin-load-failure.md`](04-plugin-load-failure.md) — if the
  worker can't drain because no plugins are loaded to produce
  artifacts (rare, but the failure shape is similar).
- [`02-secret-rotation.md`](02-secret-rotation.md) — if §5.1 leads
  back to a password regression.
- [`PLAN.md`](../../PLAN.md) §18 — Sprint 7 / P1 origin.
