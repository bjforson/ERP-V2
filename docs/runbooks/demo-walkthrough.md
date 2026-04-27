# Demo walkthrough — end-to-end inspection lifecycle

This is the manual companion to the automated e2e test in
`tests/NickERP.Inspection.E2E.Tests/FullCaseLifecycleTests.cs`. Following
these steps drives one container scan from raw FS6000 byte-drop to a
closed case with a verdict submitted to the ICUMS outbox — the same
happy-path the automated fact asserts on, run by hand.

Time budget: **under 5 minutes** once the prereqs are satisfied. If a
step takes noticeably longer, jump to *Troubleshooting* at the bottom.

---

## Prereqs (one-time)

| Item | Setting |
|---|---|
| `NICKSCAN_DB_PASSWORD` | Set to the dev Postgres password. Both apps and the migrations consume this. |
| `ConnectionStrings__Platform` | `Host=localhost;Port=5432;Database=nickerp_platform;Username=postgres;Password=$NICKSCAN_DB_PASSWORD` |
| `ConnectionStrings__Inspection` | `Host=localhost;Port=5432;Database=nickerp_inspection;Username=postgres;Password=$NICKSCAN_DB_PASSWORD` |
| Postgres 18 | Cluster running on `localhost:5432` with the two databases provisioned per `TESTING.md`. |
| Plugin DLLs staged | `modules/inspection/src/NickERP.Inspection.Web/plugins/` populated per the recipe in `TESTING.md`. |
| Inspection host | `cd modules/inspection/src/NickERP.Inspection.Web && dotnet run` — leaves a server listening on `http://localhost:5410`. |
| Demo folders | Create empty directories: `C:\inspection-demo\fs6000-incoming`, `C:\inspection-demo\icums-drop`, `C:\inspection-demo\icums-outbox`. They must be writable by the host process. |

The first time you run the host with `RunMigrationsOnStartup=true` (the
default in `Development`), it applies every migration. Subsequent runs
no-op the migration step.

---

## Step 1 — Register the location

Open <http://localhost:5410/locations>.

1. Click **New location**.
2. Code: `tema`
3. Name: `Tema Port`
4. Region: `Greater Accra`
5. Time zone: `Africa/Accra`
6. Save.

The location row should appear in the list with `IsActive=true`.

The `tema` code is load-bearing: the gh-customs rule pack maps `tema` to
the BOE port code `TMA`. Pick a different code and the port-match rule
will Skip rather than Pass — still demos correctly, just less realistic.

---

## Step 2 — Register the FS6000 scanner

Open <http://localhost:5410/scanners>.

1. Click **New scanner**.
2. Plugin: select `fs6000` from the dropdown. (`mock-scanner` is also
   present; ignore it for this demo.)
3. Location: `Tema Port`
4. Display name: `Demo FS6000`
5. Config JSON:
   ```json
   {
     "WatchPath": "C:\\inspection-demo\\fs6000-incoming",
     "PollIntervalSeconds": 2
   }
   ```
6. Save.

The row appears with `IsActive=true`. The `ScannerIngestionWorker` will
discover it on its next 60-second pass — you don't need to restart the
host.

---

## Step 3 — Register the ICUMS external system

Open <http://localhost:5410/external-systems>.

1. Click **New external system**.
2. Plugin: select `icums-gh`.
3. Display name: `Demo ICUMS Ghana`
4. Scope: `Per-location`
5. Config JSON:
   ```json
   {
     "BatchDropPath": "C:\\inspection-demo\\icums-drop",
     "OutboxPath": "C:\\inspection-demo\\icums-outbox",
     "CacheTtlSeconds": 30
   }
   ```
6. Save.

The row appears with `IsActive=true`. (The mock external system is also
selectable; pick `icums-gh` to match the demo.)

---

## Step 4 — Drop a real FS6000 triplet

The triplet is three sibling files sharing a common stem:
`{stem}high.img`, `{stem}low.img`, `{stem}material.img`. The stem is
the filename portion that the case's `SubjectIdentifier` will be set to.
Use a real container number so it can match an ICUMS BOE later.

The fastest way to produce a valid triplet is to reuse the same
synthesizer the byte-parity test uses. Run this one-liner from the
`tests/NickERP.Inspection.Scanners.FS6000.Tests/` folder (or invoke the
e2e helper at `tests/NickERP.Inspection.E2E.Tests/E2EFixtures.cs`):

```csharp
// In a one-off LINQPad / dotnet-script / xUnit harness:
NickERP.Inspection.E2E.Tests.E2EFixtures.WriteFs6000Triplet(
    @"C:\inspection-demo\fs6000-incoming",
    "MSCU8675309");
```

Or, if you have a real triplet from production, copy it into
`C:\inspection-demo\fs6000-incoming\` keeping the three sibling files.

**Filenames to verify after the drop:**

```
C:\inspection-demo\fs6000-incoming\
    MSCU8675309high.img
    MSCU8675309low.img
    MSCU8675309material.img
```

---

## Step 5 — Watch the case appear

Open <http://localhost:5410/cases>. Refresh every few seconds for up to
60 seconds (the `ScannerIngestionWorker`'s discovery interval). Within
that window:

- A new row appears with `SubjectIdentifier=MSCU8675309`.
- `LocationId` resolves to the `Tema Port` row from Step 1.
- A thumbnail image loads next to the row within ~3 seconds of the case
  appearing (the `PreRenderWorker` polls every 5 seconds in dev).

If the case row appears but no thumbnail does, see *Troubleshooting →
PreRenderWorker stuck*.

---

## Step 6 — Fetch ICUMS documents

Drop a sample BOE batch JSON into the ICUMS drop folder. The minimum
shape for one container is:

```json
{
  "BOEScanDocument": [
    {
      "Header": {
        "DeclarationNumber": "C 123456 26",
        "RegimeCode": "40",
        "ClearanceType": "IM"
      },
      "ManifestDetails": {
        "HouseBL": "HBL-2026-DEMO-001",
        "DeliveryPlace": "WTTMA1MPS3"
      },
      "ContainerDetails": {
        "ContainerNumber": "MSCU8675309"
      }
    }
  ]
}
```

Save it as `C:\inspection-demo\icums-drop\demo-batch-MSCU8675309.json`.

Click the case row in `/cases` to drill in. On the case detail page,
click **Fetch documents**. Within ~1 second:

- The **Documents** panel shows one BOE row with reference number
  `C 123456 26`.
- The **Authority checks** panel auto-populates (D3 wiring) without a
  second click — you do **not** click "Run authority checks" manually.
- The auto-fired rules show **no** `GH-PORT-MATCH` Error because the
  BOE's `DeliveryPlace` (`WTTMA1MPS3`, port code `TMA`) matches the
  Tema location.

Refresh the page. The case state should now read `Validated`.

---

## Step 7 — Assign, verdict, submit

Still on the case detail page:

1. Click **Assign me**. The case state advances to `Assigned`. A
   `ReviewSession` row gets created under your user.
2. Pick a verdict. For the demo, use **Clear** with confidence `0.9`
   and basis `demo`. Click **Set verdict**. The case advances to
   `Verdict`.
3. Pick the `Demo ICUMS Ghana` external system from the submission
   dropdown and click **Submit**. The adapter writes a JSON to the
   outbox. The case advances to `Submitted`.
4. Click **Close case**. State advances to `Closed`.

---

## Step 8 — Verify the outbox file

Inspect `C:\inspection-demo\icums-outbox\`. You should see exactly one
JSON file whose name is the SHA-256 of the submission's idempotency
inputs (a 64-char hex string), e.g.:

```
C:\inspection-demo\icums-outbox\
    A8B0F1D2... (64 hex chars).json
```

Open it. The contents should be:

```json
{
  "idempotencyKey": "<same hex>",
  "authorityReferenceNumber": "MSCU8675309",
  "submittedAtUtc": "2026-04-27T12:34:56.789+00:00",
  "instanceId": "<icums-gh instance guid>",
  "payload": {
    "caseId": "<case guid>",
    "decision": "Clear",
    "basis": "demo"
  }
}
```

---

## Step 9 — Verify the audit log

Open the Portal at <http://localhost:5400/audit>. Filter the
`Event type prefix` field to `nickerp.inspection.` and run the search.
You should see at least eight rows, in this approximate order:

| # | EventType | EntityType |
|---|---|---|
| 1 | `nickerp.inspection.case_opened` | `InspectionCase` |
| 2 | `nickerp.inspection.scan_recorded` | `Scan` |
| 3 | `nickerp.inspection.document_fetched` | `AuthorityDocument` |
| 4 | `nickerp.inspection.case_validated` | `InspectionCase` |
| 5 | `nickerp.inspection.rules_evaluated` | `InspectionCase` |
| 6 | `nickerp.inspection.case_assigned` | `InspectionCase` |
| 7 | `nickerp.inspection.verdict_set` | `Verdict` |
| 8 | `nickerp.inspection.submission_dispatched` | `OutboundSubmission` |
| 9 | `nickerp.inspection.case_closed` | `InspectionCase` |

If any of (1, 2, 3, 4, 5, 7, 8, 9) is missing, the workflow regressed —
look at the corresponding step above for the failure point. Event 6
(`case_assigned`) is missing only when verdict was set without
`AssignSelfAndStartReviewAsync` running first; the workflow service
auto-assigns in that case but this demo runs the explicit click anyway.

The event payload column carries a JSON envelope; click into a row to
inspect details.

---

## Troubleshooting

### `PreRenderWorker` stalled (no thumbnail)

- **Source bytes missing.** Check the file at
  `<StorageRoot>/source/{hash[..2]}/{hash}.png`. If absent, the FS6000
  adapter's `ParseAsync` failed silently. Confirm the triplet has all
  three sibling files and that `ContentHash` on the `inspection.scan_artifacts`
  row matches the on-disk filename (case-sensitive).
- **Tenant interceptor refused the SaveChanges.** The PreRenderWorker
  opens a fresh DI scope per cycle without setting
  `ITenantContext`; the
  `TenantOwnedEntityInterceptor` throws `InvalidOperationException`
  on the SaveChanges. Symptom: thumbnail PNG appears under
  `<StorageRoot>/render/.../thumbnail.png` but no row lands in
  `inspection.scan_render_artifacts`. Workaround for live demos: tail
  the host log for `PreRenderWorker cycle failed; backing off` —
  that's the signal. The e2e test papers over this with a single-tenant
  `ITenantContext` stub; a permanent fix is being tracked as a
  follow-up to D4 (have PreRenderWorker stamp the tenant from the
  artifact's row before SaveChanges, similar to ScannerIngestionWorker).
- **Permanently failed.** If the same artifact has 5 attempts logged
  in `inspection.scan_render_attempts` with `PermanentlyFailedAt`
  set, the worker has retired it. Re-trigger ingestion of a new
  triplet to demo a healthy render.

### Plugin didn't load (`fs6000` / `icums-gh` / `gh-customs` missing from dropdown)

- Visit `/plugins`. If you see fewer than five plugins (mock-scanner +
  mock-external + fs6000 + icums-gh + gh-customs), the loader skipped
  one. Tail the host log on startup for `Skipping {Dll}: no manifest`
  or `Plugin '{TypeCode}' requires X.Y of <Contract>; host has A.B`.
- Verify `modules/inspection/src/NickERP.Inspection.Web/plugins/` has
  the DLL **and** a `*.plugin.json` sidecar for each adapter. The
  sidecar filename is the DLL name + `.plugin.json` (e.g.
  `NickERP.Inspection.Scanners.FS6000.dll` →
  `NickERP.Inspection.Scanners.FS6000.plugin.json`).
- Re-run the staging recipe in `TESTING.md`.

### Watch-folder permission error

- The `ScannerIngestionWorker` log says
  `FS6000 WatchPath does not exist` or `Access to the path is denied`.
- Cause: the path under `WatchPath` either doesn't exist or the
  process running the host can't read it. Create the directory
  manually and grant the host's user `Modify` permission.

### `nscim_app` connection failures

If the host runs under the locked-down `nscim_app` Postgres role
(F5 slice 3, deferred), the dev-bypass user lookup will return zero
rows because tenancy hasn't been resolved yet at auth time. Symptom:
HTTP 401 on every request. Workaround: switch
`ConnectionStrings:Platform` and `ConnectionStrings:Inspection` to
`Username=postgres` for the demo. The permanent fix is tracked in
PLAN.md F6 (identity-tenancy interlock).

---

## Resetting between demo runs

Drop the test data tables (this is the cheap path; the migrations
re-apply from scratch on the next host boot):

```bash
PSQL="/c/Program Files/PostgreSQL/18/bin/psql.exe"
PGPASSWORD="$NICKSCAN_DB_PASSWORD" "$PSQL" -h localhost -U postgres -d nickerp_inspection \
  -c "TRUNCATE inspection.outbound_submissions, inspection.verdicts, inspection.analyst_reviews,
                  inspection.review_sessions, inspection.findings, inspection.authority_documents,
                  inspection.scan_render_attempts, inspection.scan_render_artifacts,
                  inspection.scan_artifacts, inspection.scans, inspection.cases CASCADE;"
```

Wipe the watch folder + ICUMS drop folder + outbox between runs:

```powershell
Remove-Item -Recurse -Force C:\inspection-demo\fs6000-incoming\*
Remove-Item -Recurse -Force C:\inspection-demo\icums-drop\*
Remove-Item -Recurse -Force C:\inspection-demo\icums-outbox\*
```

Plus the imaging store (`C:\Shared\ERP V2\.imaging-store` in dev) so
the next demo regenerates thumbnails fresh.

---

## Related material

- **Automated equivalent.** `tests/NickERP.Inspection.E2E.Tests/`. Run
  with `dotnet test --filter Category=Integration` from the repo root.
  Same lifecycle, fully scripted. Spins up an ephemeral Postgres pair
  and Inspection host on its own port, so it never collides with a live
  demo on `:5410`.
- **TESTING.md.** Source of truth for the click-through walkthrough of
  Portal v2 + Inspection v2 admin UIs.
- **ARCHITECTURE.md §7.7.** The image pipeline contract this walkthrough
  exercises (source stash → pre-render → ETag-cached HTTP serving).
