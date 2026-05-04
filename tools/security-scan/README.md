# tools/security-scan

Sprint 30 Phase V SAST tooling. Three PowerShell scripts that wrap `dotnet`-native package scanners + a regex secret-scanner. Companion to `docs/security/audit-checklist-2026.md`.

## Scripts

| Script | What it does | Audit item |
|---|---|---|
| `run-vulnerability-scan.ps1` | `dotnet list package --vulnerable --include-transitive` per project; markdown report | SEC-DEP-1 |
| `run-dependency-audit.ps1` | `dotnet list package --outdated --include-transitive` per project; markdown report | SEC-DEP-2 |
| `check-secrets.ps1` | Best-effort regex scan for AWS / GitHub / Slack tokens, hardcoded passwords, JWT bearers, private keys | SEC-SECRETS-1, SEC-SECRETS-8 |

All three are idempotent and safe to run repeatedly. None of them mutate the tree.

## Run

From the repo root:

```powershell
# vulnerable packages
.\tools\security-scan\run-vulnerability-scan.ps1

# outdated packages
.\tools\security-scan\run-dependency-audit.ps1

# secret-shaped strings
.\tools\security-scan\check-secrets.ps1
```

Each writes to `tools/security-scan/reports/{yyyy-MM-dd}-{kind}.md`. The reports directory is committed (sample reports land alongside the scripts so future operators know the expected format).

## Exit codes

- `0` — clean / no findings of triage-worthy severity
- `2` — P0 finding present (triage immediately)
- non-zero / unhandled — exception during scan

## What this tooling is NOT

- A replacement for production SAST during Phase V proper. Use truffleHog / gitleaks / sonar / snyk for the real audit.
- A pre-commit hook (could be wired up later, scope is intentionally narrow for Sprint 30).
- A dependency-license auditor (covered by SEC-DEP-3 manually for now; tool integration deferred).

## Maintenance

- New secret patterns: append to `$patterns` array in `check-secrets.ps1`.
- New excluded directories: append to `$excludeDirsRegex` in both `run-vulnerability-scan.ps1` and `check-secrets.ps1`.
- Tooling changes get a corresponding update in `docs/security/audit-checklist-2026.md` SEC-DEP / SEC-SECRETS items.

## Sample reports

`reports/2026-05-04-vulnerabilities.md` (committed) is a sample run from Sprint 30 against the v2 tree at the time of authoring. It establishes the baseline format and shows the expected output. Subsequent runs append new dated reports beside it.
