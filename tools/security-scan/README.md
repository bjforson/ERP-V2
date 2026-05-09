# tools/security-scan

Sprint 30 Phase V SAST tooling, augmented Sprint 60 with a register-walking audit-checklist runner. PowerShell scripts that wrap `dotnet`-native package scanners + a regex secret-scanner + a checklist-walking audit runner. Companion to `docs/security/audit-checklist-2026.md`.

## Scripts

| Script | What it does | Audit item |
|---|---|---|
| `run-audit.ps1` | Sprint 60 Phase B1 — walks the full audit-checklist; runs per-item handlers where automatable; emits ticked artifact under `reports/audit-{site}-{date}.md`. Manual items are passed through as `[manual]` with the original Verify prose. SEC-DEP and SEC-SECRETS items chain to the per-tool reports below. | All ~89 SEC-* register items |
| `run-vulnerability-scan.ps1` | `dotnet list package --vulnerable --include-transitive` per project; markdown report | SEC-DEP-1 |
| `run-dependency-audit.ps1` | `dotnet list package --outdated --include-transitive` per project; markdown report | SEC-DEP-2 |
| `run-license-audit.ps1` | `dotnet list package --include-transitive` + cross-reference against `license-allowlist.json`; flags non-allowlisted licenses | SEC-DEP-3 |
| `run-trufflehog.ps1` | Wraps `trufflehog` (production-grade SAST); scans full git history with verifier mode for live-secret discrimination | SEC-SECRETS-1, SEC-SECRETS-8 |
| `check-secrets.ps1` | (Legacy) best-effort regex scan; preserved as a fast smoke check. The Phase V canonical tool is `run-trufflehog.ps1`. | SEC-SECRETS-1 (smoke only) |

All scripts are idempotent and safe to run repeatedly. None of them mutate the tree.

## Audit runner — `run-audit.ps1`

`run-audit.ps1` is the Phase V execution-driver: instead of an operator reading 89 items cold start and grepping by hand, the runner walks the checklist, executes per-SEC-* verification commands where automatable, ticks `[x]` for pass / `[!]` for finding / `[ ]` for skip / `[manual]` for operator-judgement, and emits `audit-{site}-{date}.md` matching the checklist's How-to-use shape.

Example invocations:

```powershell
# Default: dev site, no DB, read existing reports for chained checks
.\tools\security-scan\run-audit.ps1

# Tag artifact for the pilot site
.\tools\security-scan\run-audit.ps1 -Site pilot

# Run SQL-shaped checks (SEC-TENANT-3/5/6/13, SEC-DB-3) against a real DB
.\tools\security-scan\run-audit.ps1 -ConnectionString "postgres://postgres:pw@localhost:5432/nickerp_platform"

# Single category (e.g. dependency hygiene)
.\tools\security-scan\run-audit.ps1 -Filter DEP

# Trigger sibling scanners first (slow — runs vuln + license + trufflehog before reading their reports)
.\tools\security-scan\run-audit.ps1 -RunChained

# Parse only — print categorisation, no artifact
.\tools\security-scan\run-audit.ps1 -DryRun
```

### Status semantics

| Status | Marker | Meaning |
|---|---|---|
| pass    | `[x]` | Automated handler verified the expectation. |
| fail    | `[!]` | Automated handler found a violation. P0/P1 fail = exit 2. |
| skip    | `[ ]` | Handler ran but lacked a prerequisite (e.g. no `-ConnectionString` for a SQL probe, sibling scanner self-skipped, advisory check requires operator interpretation). Does NOT count as a finding. |
| manual  | `[ ]` (with `[manual]` tail) | No handler exists for this item. Operator verifies by hand against the artifact's preserved Verify prose. |
| xref    | `[ ]` (with `[xref → ...]` tail) | Item is a "See SEC-X-N" cross-reference. Tick the target. |

### Adding new automation

`docs/security/audit-checklist-2026.md` is **read-only input**. New automation handlers go in this script's `$ItemHandlers` table — add a row mapping `SEC-CAT-N → { handler-scriptblock }` and add the handler function above. Do not edit the checklist.

### Exit codes

- `0` — clean run; all P0+P1 automated checks pass (or are skipped/manual).
- `1` — pre-flight failure (e.g. checklist absent).
- `2` — at least one P0 or P1 automated check failed (CI-blocking).
- `3` — checklist parser failure (file shape changed unexpectedly).

P2/P3 findings DO NOT block exit; they surface in the artifact only. This matches the checklist's own §Phase V exit criteria — P0/P1 must pass; P2/P3 backlog OK.

## Run

From the repo root:

```powershell
# vulnerable packages (SEC-DEP-1)
.\tools\security-scan\run-vulnerability-scan.ps1

# outdated packages (SEC-DEP-2)
.\tools\security-scan\run-dependency-audit.ps1

# license posture per allowlist (SEC-DEP-3)
.\tools\security-scan\run-license-audit.ps1

# secret scan with live-key verification (SEC-SECRETS-1, SEC-SECRETS-8)
.\tools\security-scan\run-trufflehog.ps1
.\tools\security-scan\run-trufflehog.ps1 -Mode tree           # fast: working-tree only
.\tools\security-scan\run-trufflehog.ps1 -OnlyVerified        # less noisy: live secrets only

# legacy regex smoke check
.\tools\security-scan\check-secrets.ps1
```

Each writes to `tools/security-scan/reports/{yyyy-MM-dd}-{kind}.md`. The reports directory is committed (sample reports land alongside the scripts so future operators know the expected format).

## Allowlist (license audit)

`license-allowlist.json` is the canonical list. v0 admits MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC, MS-PL, MS-EULA, Unlicense. New entries require a one-sentence rationale and a bumped `version` field. Out-of-allowlist license = SEC-DEP-3 P1 finding.

## Exit codes

- `0` — clean / no findings of triage-worthy severity
- `2` — P0 finding present (triage immediately)
- non-zero / unhandled — exception during scan

## What this tooling is NOT

- A pre-commit hook (could be wired up later; scope is intentionally narrow).
- A replacement for sonar / snyk if the team adopts a managed SAST product post-pilot. trufflehog covers secrets; sonar / snyk would cover code-quality + dependency posture in a single dashboard.

## Maintenance

- New secret patterns: append to `$patterns` array in `check-secrets.ps1` (legacy smoke). The trufflehog wrapper inherits trufflehog's built-in detector set; new detectors arrive via tool upgrades.
- New excluded directories: append to `$excludeDirsRegex` in `run-vulnerability-scan.ps1` and `check-secrets.ps1`; for `run-trufflehog.ps1` edit the `$excludesFile` array near the top.
- New allowlisted licenses: append to `license-allowlist.json` `allowed` AND `rationale` maps; bump `version`. Commit message MUST link the legal review.
- Tooling changes get a corresponding update in `docs/security/audit-checklist-2026.md` SEC-DEP / SEC-SECRETS items.

## Sample reports

`reports/2026-05-04-vulnerabilities.md` (committed) is a sample run from Sprint 30 against the v2 tree at the time of authoring. It establishes the baseline format and shows the expected output. Subsequent runs append new dated reports beside it.
