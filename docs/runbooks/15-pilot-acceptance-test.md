# Pilot acceptance test — running it post-deploy

> Operator-initiated. Run after every pilot deploy and before opening a
> tenant up to real traffic. Confirms the system has truly demonstrated
> end-to-end correctness on the deployed build, not just that the
> services started cleanly.

---

## 1. Symptom

n/a — operator-initiated. Trigger when:

- Standing up a new pilot tenant for the first time.
- Re-deploying after any change that touches the audit pipeline,
  scanner adapters, edge replay, the case workflow, the outbound
  dispatcher, or the multi-tenant invariant probe.
- Before the dashboard at `/admin/pilot-readiness` is shown to the
  operator running the pilot.

The acceptance run is the **executable mirror** of the dashboard's
5 readiness gates. It walks one tenant through the realistic flow that
flips every gate Pass, then asserts the production
`PilotReadinessService` reports them Pass — proving the gates aren't
silently stuck on a deploy regression.

## 2. Severity

| Failure | Severity ceiling | Why |
|---|---|---|
| Acceptance run fails on a fresh deploy | **P1** (block pilot open-up) | the gates the operator dashboard reports on are wrong |
| Multi-tenant invariant gate Fails | **P1** | tenant isolation regression — never expose tenant data to a foreign tenant |
| One scenario gate fails after green build | **P2** | one capability silently lost — find it before traffic arrives |
| Test infrastructure failure (`NICKSCAN_DB_PASSWORD` unset, dev Postgres down) | **P3** | not a system failure — fix the harness |

## 3. Quick triage

The acceptance suite runs four xUnit facts against the live build's
test assemblies. They share a single fixture
(`PilotAcceptanceFixture`) that stands up a pair of throwaway
Postgres databases on `localhost:5432`, applies migrations, provisions
two tenants, and exposes scenario helpers:

| Test | What it proves |
|---|---|
| `Empty_dev_DB_all_5_gates_NotYetObserved` | A fresh state reports the four observable gates as `NotYetObserved`; the multi-tenant invariant gate passes (RLS isolation trivial; system-context register matches; cross-tenant export refuses unknown ids). |
| `After_full_pilot_scenario_all_5_gates_Pass` | After driving the full scenario for one tenant, the production `PilotReadinessService` reports every gate `Pass`. |
| `Multi_tenant_isolation_holds_under_concurrent_scenario` | Two tenants drive the scenario in parallel; each tenant's gates pass independently; tenant A's audit-event proofs do not collide with tenant B's; the cross-tenant invariant probe runs against B (not single-tenant trivial-pass). |
| `Synthetic_case_does_not_satisfy_decisioned_real_case_gate` | A case with `IsSynthetic=true` does not flip the analyst gate; a follow-up real case does. |

A green acceptance run = the system is genuinely ready for pilot
traffic. A red run = stop opening up; triage from §5 below.

## 4. Diagnostic commands

> All commands run from the v2 worktree root
> (`C:\Shared\ERP V2\` or a worktree under `.worktrees/`). Use
> double-quoted paths because the parent contains a space.

### 4.1. Confirm preconditions

```powershell
# Postgres reachable on :5432, dev role password set:
$env:NICKSCAN_DB_PASSWORD     # must be non-empty
psql -U postgres -d postgres -c "SELECT now();"   # must succeed

# .NET SDK present (any 10.x; we ship on 10.0.x):
dotnet --version
```

### 4.2. Run the acceptance suite

```powershell
# From the worktree root:
dotnet test "tests\NickERP.Inspection.E2E.Tests\NickERP.Inspection.E2E.Tests.csproj" `
  --filter "Category=PilotAcceptance" `
  --logger "console;verbosity=normal"
```

Expected wall-clock: ~50–60s on a warm build, ~90s cold.
Expected outcome: `Total tests: 4` / `Passed: 4` / `Failed: 0`.

### 4.3. Run the bunit page-render check

```powershell
# Proves the dashboard page renders all-green when the readiness
# service receives the same shape of mock data the integration
# scenario produces. Fast (~6s).
dotnet test "tests\NickERP.Platform.Tests\NickERP.Platform.Tests.csproj" `
  --filter "FullyQualifiedName~PilotReadinessPageBunitTests" `
  --logger "console;verbosity=normal"
```

Expected outcome: `Total tests: 2` / `Passed: 2`.

### 4.4. Inspect the live dashboard

```text
GET https://<pilot-host>/admin/pilot-readiness
```

Sign in as a tenant admin. The dashboard renders 5 gate cards:

- Scanner adapter wired
- Edge round-trip
- Analyst decisioned a real case
- External system round-trip
- Multi-tenant invariants

Each gate displays one of `PASS` / `NOT YET OBSERVED` / `FAIL`. The
multi-tenant invariant card additionally renders three sub-pills
(`rls_read_isolation`, `system_context_register`,
`cross_tenant_export_gate`).

## 5. What passing looks like

### Acceptance suite

```
Test run for C:\...\NickERP.Inspection.E2E.Tests.dll (.NETCoreApp,Version=v10.0)
[xUnit.net]   Discovering: NickERP.Inspection.E2E.Tests
[xUnit.net]   Discovered:  NickERP.Inspection.E2E.Tests
[xUnit.net]   Starting:    NickERP.Inspection.E2E.Tests
  Passed NickERP.Inspection.E2E.Tests.PilotAcceptanceTests.Empty_dev_DB_all_5_gates_NotYetObserved [6 s]
  Passed NickERP.Inspection.E2E.Tests.PilotAcceptanceTests.Multi_tenant_isolation_holds_under_concurrent_scenario [15 s]
  Passed NickERP.Inspection.E2E.Tests.PilotAcceptanceTests.Synthetic_case_does_not_satisfy_decisioned_real_case_gate [19 s]
  Passed NickERP.Inspection.E2E.Tests.PilotAcceptanceTests.After_full_pilot_scenario_all_5_gates_Pass [9 s]
[xUnit.net]   Finished:    NickERP.Inspection.E2E.Tests

Test Run Successful.
Total tests: 4
     Passed: 4
 Total time: ~53 Seconds
```

### Live dashboard (after one tenant has run a full pilot scenario)

- Five green PASS pills.
- Multi-tenant invariants card shows three green sub-pills:
  `rls_read_isolation`, `system_context_register`,
  `cross_tenant_export_gate`.
- Each PASS gate either has a "Proof event:" link to the seeding
  audit row, or a "Latest non-synthetic verdict: case X at <time>"
  hint.
- Last-refreshed timestamp under one minute old (auto-refresh runs
  every 30 seconds while the page is open).

## 6. How to interpret a failing gate

| Gate | Failure | Likely root cause |
|---|---|---|
| `gate.scanner.adapter` | `NotYetObserved` after acceptance run | The `nickerp.inspection.scan_recorded` audit emitter in `CaseWorkflowService.IngestArtifactAsync` has regressed; or `audit.events` is being filtered by a stale RLS policy. |
| `gate.edge.roundtrip` | `NotYetObserved` after acceptance run | The `inspection.scan.captured` audit row is not being written by `EdgeReplayEndpoint`; or the test fixture failed to insert a synthetic edge replay. |
| `gate.analyst.decisioned_real_case` | `NotYetObserved` after acceptance run with non-synthetic data | `IInspectionPilotProbeDataSource.HasDecisionedRealCaseAsync` is failing — most often a Verdicts row missing because the workflow service's `SetVerdictAsync` regressed. |
| `gate.external_system.roundtrip` | `NotYetObserved` after acceptance run | An `OutboundSubmission` row in `Status='accepted'` with `LastAttemptAt` set is not present; check `OutboundSubmissionDispatchWorker` for backlog (runbook 05 §icums-outbox-backlog). |
| `gate.multi_tenant.invariants` | `Fail` with `rls_read_isolation:fail(...)` | RLS policy missing or `app.tenant_id` not pushed on connection — runbook 02 §5 covers the rotation steps; check `TenantConnectionInterceptor` is registered. |
| `gate.multi_tenant.invariants` | `Fail` with `system_context_register:fail(register drift...)` | `docs/system-context-audit-register.md` has not been updated to reflect a new `SetSystemContext` caller (or removed one). Sprint 57's territory; **not** a security hole on its own — it just means the audit register is stale. |
| `gate.multi_tenant.invariants` | `Fail` with `cross_tenant_export_gate:fail(...)` | `TenantExportService.DownloadExportAsync` returned a non-null result for a random Guid — a real isolation hole. **Stop the deploy and triage immediately.** |

## 7. Resolution

The acceptance suite is a probe, not a remediation tool. When it
flags a failure:

1. **Capture the test output** verbatim. Each failing test prints
   the offending gate's `Note` field — that's the load-bearing
   diagnostic.
2. **Cross-reference with §6** — most failures map onto an existing
   runbook (02-secret-rotation for RLS / role posture, 05 for ICUMS
   outbox, 06 for edge replay, 03 for the imaging pipeline if the
   scanner gate trails everything else).
3. **Do not weaken security posture** to make the test pass. Per the
   project's CLAUDE.md hard-rule §5: every weakening
   (`AllowAnonymous`, broadened DB grants, lowered hashing cost, RLS
   bypass, etc.) needs explicit user confirmation, and at least one
   non-weakening alternative must be presented first. If the test
   says the cross-tenant export gate is broken, the fix is a real
   patch — not a relaxed assertion.

## 8. Verification

After resolving the failure, re-run §4.2. The full acceptance suite
must come back green. The dashboard at `/admin/pilot-readiness`
should now report PASS for whichever gate flipped.

A "Restore minimal-privilege state" check rounds out the runbook —
same shape as runbook 01 §6:

```powershell
psql -U postgres -d postgres -c `
  "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = 'nscim_app';"
# Expected: super=f, bypassrls=f.
```

If you find yourself wanting to weaken this — **stop**. Read
CLAUDE.md hard-rule §5.

## 9. Aftermath

- File a `DEFERRED_ACTIONS.md` entry for any gate that surfaced a
  systemic issue (e.g. recurring `system_context_register` drift
  → schedule the Sprint 57 audit register sweep sooner).
- If the acceptance run found the issue **before** the operator
  opened up to traffic, log it as a gate WIN: this is exactly why
  Sprint 53 ships.
- If the acceptance run **missed** a regression that hit production
  later, file a follow-up issue under "Sprint 53 acceptance suite
  gap" with a repro path so a new scenario test can be added.

## 10. References

- [`../../platform/NickERP.Platform.Tenancy.Database/Services/PilotReadinessService.cs`](../../platform/NickERP.Platform.Tenancy.Database/Services/PilotReadinessService.cs)
  — production probe service the suite drives.
- [`../../platform/NickERP.Platform.Tenancy/Pilot/PilotReadinessGate.cs`](../../platform/NickERP.Platform.Tenancy/Pilot/PilotReadinessGate.cs)
  — the 5 stable gate ids.
- [`../../tests/NickERP.Inspection.E2E.Tests/PilotAcceptanceFixture.cs`](../../tests/NickERP.Inspection.E2E.Tests/PilotAcceptanceFixture.cs)
  — scenario engine.
- [`../../tests/NickERP.Inspection.E2E.Tests/PilotAcceptanceTests.cs`](../../tests/NickERP.Inspection.E2E.Tests/PilotAcceptanceTests.cs)
  — the four scenario facts.
- [`../../apps/portal/Components/Pages/PilotReadiness.razor`](../../apps/portal/Components/Pages/PilotReadiness.razor)
  — the live dashboard at `/admin/pilot-readiness`.
- [`02-secret-rotation.md`](02-secret-rotation.md) — DB role rotation
  for RLS posture failures.
- [`05-icums-outbox-backlog.md`](05-icums-outbox-backlog.md) —
  outbound dispatcher backlog handling.
- [`06-edge-node-stalled.md`](06-edge-node-stalled.md) — edge
  replay-not-draining diagnostics.
