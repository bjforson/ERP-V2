# tools/security-scan

Sprint 30 Phase V SAST tooling. Three PowerShell scripts that wrap `dotnet`-native package scanners + a regex secret-scanner. Companion to `docs/security/audit-checklist-2026.md`.

## Scripts

| Script | What it does | Audit item |
|---|---|---|
| `run-vulnerability-scan.ps1` | `dotnet list package --vulnerable --include-transitive` per project; markdown report | SEC-DEP-1 |
| `run-dependency-audit.ps1` | `dotnet list package --outdated --include-transitive` per project; markdown report | SEC-DEP-2 |
| `run-license-audit.ps1` | `dotnet list package --include-transitive` + cross-reference against `license-allowlist.json`; flags non-allowlisted licenses | SEC-DEP-3 |
| `run-trufflehog.ps1` | Wraps `trufflehog` (production-grade SAST); scans full git history with verifier mode for live-secret discrimination | SEC-SECRETS-1, SEC-SECRETS-8 |
| `check-secrets.ps1` | (Legacy) best-effort regex scan; preserved as a fast smoke check. The Phase V canonical tool is `run-trufflehog.ps1`. | SEC-SECRETS-1 (smoke only) |

All scripts are idempotent and safe to run repeatedly. None of them mutate the tree.

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
