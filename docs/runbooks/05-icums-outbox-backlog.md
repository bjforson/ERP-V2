# Runbook 05 — ICUMS file-based outbox backlog

> **Scope.** ERP V2's Ghana-ICUMS adapter
> (`modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh`)
> writes verdict envelopes as JSON files into a configured `OutboxPath`
> for downstream pickup by ICUMS-side tooling. When the downstream
> pickup stalls (network down, ICUMS endpoint down, key rotated
> mid-flight, file permissions, disk full), the outbox grows
> unbounded. This runbook covers detection, safe inspection, manual
> drain, and re-signing.
>
> **Why "file-based":** v1 NSCIM never wired direct HTTP to ICUMS;
> verdicts always flowed through a filesystem outbox at
> `C:\Shared\NSCIM_PRODUCTION\Data\ICUMS\Outbox`. v2 mirrors that
> deployment topology — see `IcumsGhAdapter.SubmitAsync` in
> `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/IcumsGhAdapter.cs`.
>
> **Sister docs:** [`04-plugin-load-failure.md`](04-plugin-load-failure.md)
> — if the IcumsGh plugin failed to load, this runbook doesn't apply
> (you'd see no submissions at all, not a backlog).

---

## 1. Symptom

Any of:

- **Operator-side complaint:** "ICUMS hasn't acknowledged anything
  in the last hour." Submissions are happening, ICUMS isn't reading
  them.
- **`outbound_submissions`** table rows accumulating with no
  matching ICUMS-side ack (today this surfaces as case-level state
  not advancing past `Submitted`).
- **Disk usage on the outbox volume** climbing past expected
  baseline. The outbox files are small (≤5 KB each), so this is a
  high-cardinality, not high-byte, problem — but enough files is
  enough.
- **Audit log** shows a wave of `IExternalSystemAdapter.SubmitAsync`
  successes (the host's view: "I wrote the file, all good") with no
  downstream pickup.

## 2. Severity

| Pattern | Severity | Response window |
|---|---|---|
| Backlog < 100 files, < 1 h old | P3 | log, watch, ignore |
| Backlog 100–1000 files, < 4 h old | P2 | inside 4 h |
| Backlog > 1000 files OR > 4 h old | P1 | inside 1 h |
| Backlog growing AND host can't write to outbox (disk full / perms) | P1 | inside 30 min — host is silently failing submissions |

The host's failure mode for "outbox write failed" is to return a
`SubmissionResult(false, ..., "Outbox write failed: ...")` to the
caller. That's loud. The silent failure is "outbox write succeeded
but downstream pickup never runs" — files just accumulate. That's
this runbook.

## 3. Quick triage (60 seconds)

```bash
# Find the configured outbox path.
psql -U postgres -d nickerp_inspection -c \
  'SELECT "Id", "DisplayName", "ConfigJson"::jsonb -> '"'"'OutboxPath'"'"' AS outbox_path
   FROM inspection.external_system_instances
   WHERE "TypeCode" = '"'"'icums-gh'"'"';'
```

The path comes from the `external_system_instances.ConfigJson`
column at the row scoped to your tenant. Multiple instances are
possible (one per CF Access tenant); each is its own outbox.

```bash
# Pick one, count the files.
OUTBOX_PATH="C:/Shared/ICUMS/Outbox"  # replace with the actual path
ls "$OUTBOX_PATH"/*.json 2>/dev/null | wc -l

# How old is the oldest file?
ls -lt "$OUTBOX_PATH"/*.json 2>/dev/null | tail -1
```

A handful of files dating back minutes is healthy turnover. Hundreds
dating back hours is the backlog this runbook addresses.

## 4. Diagnostic commands

### 4.1 Read the host's perspective

The adapter's success path writes a file and reports
`SubmissionResult.Accepted = true`. The host has no notion of
"acknowledged by ICUMS" — that's an out-of-band downstream pickup.

```bash
# Recent submissions from this host's POV.
psql -U postgres -d nickerp_inspection -c \
  'SELECT "Id", "ExternalSystemInstanceId", "AuthorityReferenceNumber",
          "IdempotencyKey", "Status", "CreatedAt", "ResponseJson"
   FROM inspection.outbound_submissions
   ORDER BY "CreatedAt" DESC LIMIT 20;'
```

A `Status = Accepted` row with `ResponseJson` containing
`"outboxPath": "..."` means the host wrote the file and trusts the
filesystem from there.

### 4.2 Inspect the outbox

```bash
OUTBOX_PATH="C:/Shared/ICUMS/Outbox"  # use the real path from §3

# How many files?
ls "$OUTBOX_PATH" 2>/dev/null | wc -l

# Age distribution (oldest first).
ls -lt "$OUTBOX_PATH"/*.json 2>/dev/null | tail -10

# Total bytes.
du -sh "$OUTBOX_PATH" 2>&1
```

### 4.3 Read one outbox file to confirm format

The envelope shape is fixed by `IcumsGhAdapter.SubmitAsync`:

```bash
ls "$OUTBOX_PATH"/*.json | head -1 | xargs jq . 2>&1 | head -25
```

Expected fields:

```json
{
  "idempotencyKey": "<sha256 / opaque>",
  "authorityReferenceNumber": "<BOE / CMR / IM ref>",
  "submittedAtUtc": "2026-04-29T...",
  "instanceId": "<guid>",
  "payload": { ... }
}
```

If a file is missing `idempotencyKey` or `submittedAtUtc`, it's not
an ERP V2 emission — it might be a dropped manual file, an old v1
artifact, or a failed atomic-write residue (`*.tmp`).

### 4.4 Look for `.tmp` residue

```bash
ls "$OUTBOX_PATH"/*.tmp 2>&1
```

`SubmitAsync` writes to `<idempotencyKey>.json.tmp` and renames
atomically. A `.tmp` file means a host crash or process kill
interrupted a submission. **These are not safe to ship downstream**
— they may be partially written. They are safe to delete.

### 4.5 Confirm the host can write

```bash
# Sentinel write — same shape as the adapter's pre-flight Probe.
echo '{}' > "$OUTBOX_PATH/.healthcheck-$$"
ls -la "$OUTBOX_PATH/.healthcheck-$$"
rm "$OUTBOX_PATH/.healthcheck-$$"
```

Failure here means the **host process** can't write to the path —
the running adapter would return `SubmissionResult(Accepted=false,
Error="Outbox write failed: ...")` for every call. That's a different
incident shape from "files pile up unread" but worth ruling out
before the manual drain.

### 4.6 Confirm the downstream pickup is broken

The downstream pickup is an out-of-band ICUMS-side process
(historically: a v1 hot folder watcher, an SFTP cron, or a manual
operator transfer — the exact mechanism is a deployment-time choice,
not part of v2). Confirm by:

- Talking to the downstream operator. "Are you reading from
  `<OutboxPath>`?"
- Looking at the modification times of the files (§4.2). If the
  oldest file is > 1 h old and you'd expect reads within minutes, the
  reader is not reading.
- Checking for any side-channel signal the downstream has been
  configured to send (e.g. a tombstone file written back into the
  outbox after pickup). v2 doesn't define this — if the deploy uses
  one, document it under §8 Resolutions for next time.

## 5. Resolution

### 5.1 The downstream is back — let normal pickup drain

If §4.6 confirms the downstream pickup has resumed, do nothing.
File mtimes will catch up; the backlog will drain. Re-check §4.2 in
30 min to confirm.

### 5.2 The downstream is permanently changed (path moved, format changed)

Resolution: **don't double-send**. Re-pointing the host's outbox to
a new path and shipping the backlog there manually risks a duplicate
submission. The adapter's idempotency model is per-`idempotencyKey`,
so a duplicate file with the same key is safe — but the downstream's
de-dup is not the adapter's contract.

Steps:

1. Quiesce new submissions:
   ```bash
   # Mark the External System Instance as inactive so the host stops
   # routing new submissions to it. This is the supported off-switch;
   # it doesn't delete configuration.
   psql -U postgres -d nickerp_inspection -c \
     'UPDATE inspection.external_system_instances
      SET "IsEnabled" = false
      WHERE "TypeCode" = '"'"'icums-gh'"'"' AND "Id" = '"'"'<instance-id>'"'"';'
   ```
   (Confirm column name from
   `modules/inspection/src/NickERP.Inspection.Core/Entities/ExternalSystemInstance.cs`
   in your tree before running — the schema column might be
   `IsActive` or `IsEnabled` depending on the post-Sprint-7 state.)
2. Reconfigure the adapter with the new `OutboxPath`:
   ```bash
   psql -U postgres -d nickerp_inspection -c \
     "UPDATE inspection.external_system_instances
      SET \"ConfigJson\" = jsonb_set(\"ConfigJson\"::jsonb, '{OutboxPath}',
                                     '\"<NEW_PATH>\"', true)::text
      WHERE \"TypeCode\" = 'icums-gh' AND \"Id\" = '<instance-id>';"
   ```
3. Move (don't copy) the backlog files to the new path:
   ```bash
   mv "$OLD_OUTBOX/"*.json "$NEW_OUTBOX/"
   ```
4. Re-enable the instance and verify §6.

### 5.3 Drain manually — submit each file to the downstream out-of-band

Sometimes the downstream is "back, but won't catch up on its own."
Manually pushing each file (e.g. into an HTTP endpoint, an SFTP
drop, whatever the downstream contract is) is acceptable as long as
**the same `idempotencyKey` is used**. The downstream owns dedup; the
adapter's `idempotencyKey` is your handle.

```bash
# Iterate the outbox in submitted-order so retries hit oldest first.
for f in $(ls -tr "$OUTBOX_PATH"/*.json); do
  KEY=$(jq -r .idempotencyKey "$f")
  # Out-of-band push is downstream-specific; example for an HTTP target:
  # curl -X POST "$ICUMS_RECEIVE_URL" \
  #   -H "Content-Type: application/json" \
  #   --data-binary "@$f" || break
  echo "(would push $KEY from $f)"
done
```

**Do not delete files until the downstream confirms receipt.** If a
file fails to push, leave it; the next manual drain or the resumed
downstream picks it up.

### 5.4 The signing key rotated mid-flight

Symptom: §4.6 shows the downstream **is** reading, but **rejecting**
files written before a key rotation. v2's IcumsGh adapter does not
sign payloads today — the envelope is plain JSON (see §4.3) — so
this case is currently moot. **If a future contract bump adds
signing**, the resolution is:

1. Identify the cutoff mtime (the moment the new key took effect).
2. For files with mtime older than the cutoff, re-emit with the new
   signature. The simplest way is to re-trigger submission from the
   `outbound_submissions` row (the host's `SubmitAsync` will rewrite
   the file with the current key — same `idempotencyKey`, idempotent
   by contract).
3. Old files (with the old signature) can be deleted only after
   replacement files are written. **Don't delete first** — atomic
   rewrite isn't atomic across this boundary.

This sub-runbook is the seed for an actual rotation procedure when
signing lands. Until then, document any deviation in §7.

### 5.5 Disk full / outbox path not writable

Symptom: §4.5 fails, OR `outbound_submissions.Status = Failed` rows
with `Error` like `Outbox write failed: ... There is not enough space
on the disk`.

Resolution: free the disk. The outbox files themselves are tiny
(a few KB each) — disk full almost always means *another* tenant or
service on the same volume. Move that, not the outbox.

If the outbox volume is genuinely the wrong sizing for the case
volume:

1. Provision a larger volume.
2. Apply §5.2 (reconfigure adapter, move backlog).
3. File a follow-up to size the outbox volume in the deploy plan
   so this doesn't recur.

### 5.6 Nuclear option — the backlog is unrecoverable junk

Sometimes the backlog is from a test environment leak, a bad config
that wrote thousands of garbage files, or a long-resolved case set
that the downstream has explicitly disowned. Deleting the backlog
is acceptable — but **only after** confirming with the downstream
that they don't want them, and **only after** capturing what was
deleted for the postmortem.

```bash
# Manifest of what's about to disappear — keep this.
ls "$OUTBOX_PATH"/*.json | wc -l > /tmp/outbox-manifest-$(date +%Y%m%d-%H%M%S).count
ls "$OUTBOX_PATH"/*.json    > /tmp/outbox-manifest-$(date +%Y%m%d-%H%M%S).list

# Delete.
rm "$OUTBOX_PATH"/*.json
rm "$OUTBOX_PATH"/*.tmp 2>/dev/null
```

The `outbound_submissions` rows in Postgres remain — the deletion is
filesystem-only. Future analysts can still trace "we submitted X at
time T, the file was deleted at time U with operator approval."

### 5.7 Restore minimal-privilege state

If §5.5 required elevating to a different account to free disk:

- Confirm the host service account is still the only one writing to
  `OutboxPath`. The downstream's reader account should have **read-only**
  rights on the path; the host's writer account should have **write**.
  No account should need `Modify`/`Delete` from outside the host
  process.
  ```powershell
  # Check the ACL on the outbox path.
  Get-Acl "$OUTBOX_PATH" | Format-List Owner, Access
  ```
- Confirm `nscim_app` posture in Postgres is unchanged (per
  [`02-secret-rotation.md`](02-secret-rotation.md) §5.6).

## 6. Verification

After any §5 path:

1. **File count is trending down.** Re-run §4.2 after 5 min, 15 min,
   30 min. The slope should be negative (downstream is reading) or
   zero with a manual drain in progress (you're pushing).
2. **No `.tmp` residue.** §4.4 returns nothing.
3. **No `outbound_submissions` rows in `Failed` state for the last
   hour.**
   ```bash
   psql -U postgres -d nickerp_inspection -c \
     'SELECT COUNT(*) FROM inspection.outbound_submissions
      WHERE "Status" = '"'"'Failed'"'"'
        AND "CreatedAt" > now() - interval '"'"'1 hour'"'"';'
   ```
4. **A fresh end-to-end submission lands.** Trigger a real verdict
   submission (e.g. via the analyst UI), watch for the
   `outbound_submissions` row to flip to `Accepted` and the file to
   appear in `OutboxPath`.

## 7. Aftermath

### 7.1 Postmortem template

```
## ICUMS outbox backlog: <YYYY-MM-DD HH:MM>
- Detection: operator-side complaint | disk alert | manual triage
- Root cause: downstream-stalled | downstream-moved | disk-full | host-write-failure | signing-rotation
- Backlog peak (file count): <N>
- Backlog peak (oldest mtime): <timestamp>
- Resolution path: §5.1 | §5.2 | §5.3 | §5.4 | §5.5 | §5.6
- Were any submissions deleted under §5.6? <yes / no — if yes, manifest path>
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 7.2 Who to notify

Single-engineer system today: capture in `CHANGELOG.md` and update
any operator-facing runbook on the downstream side. If the
downstream contract changes (path, format, signing) is in scope,
that's a code change to the IcumsGh adapter and gets a CHANGELOG
entry against the plugin's `version` bump.

## 8. References

- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/IcumsGhAdapter.cs`
  — `SubmitAsync` is the canonical source for envelope shape and
  outbox file naming.
- `modules/inspection/plugins/NickERP.Inspection.ExternalSystems.IcumsGh/plugin.json`
  — config schema, lists `BatchDropPath` and `OutboxPath` as required.
- v1 reference — `C:\Shared\NSCIM_PRODUCTION\src\NickScanCentralImagingPortal.Services\ImageAnalysis\ImageAnalysisOrchestratorService.cs`
  is the v1 implementation of the same outbox pattern (read-only
  reference; the v2 plugin is a clean port, not a shared file). The v1
  default path was `C:\Shared\NSCIM_PRODUCTION\Data\ICUMS\Outbox`.
- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) §6.2 —
  `IExternalSystemAdapter` contract (what the host expects from
  any submit-side adapter).
- [`04-plugin-load-failure.md`](04-plugin-load-failure.md) — if the
  IcumsGh plugin won't load at all, you'll see no submissions, not a
  backlog.
- [`PLAN.md`](../../PLAN.md) §18 — Sprint 7 / P1 origin.

### 8.1 Future work

The signing-key rotation case (§5.4) is a placeholder — current v2
does not sign envelopes. When signing lands, this section becomes a
real runbook step. File a CHANGELOG entry and update §5.4 then.

A "Phase 2" for this runbook would be wiring an HTTP-direct path to
ICUMS (instead of file-based outbox), with the file outbox kept as
the disconnected-operation fallback. That's a `ROADMAP.md` decision,
not a runbook decision.
