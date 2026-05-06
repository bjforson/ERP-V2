# NickERP v2 license audit report

**Date (UTC):** 2026-05-06
**Tool:** `run-license-audit.ps1` + `dotnet list package --include-transitive`
**Allowlist:** `tools/security-scan/license-allowlist.json` (version 2026-05-06)
**Allowed licenses:** MIT, Apache-2.0, BSD-3-Clause, BSD-2-Clause, ISC, MS-PL, MS-EULA, PostgreSQL, Unlicense
**Unique (package, version) pairs:** 206

Reference: docs/security/audit-checklist-2026.md SEC-DEP-3.

---

## Summary

| Bucket | Count |
|---|---|
| Allowed | 205 |
| Non-allowlisted licenses | 0 |
| Unknown / missing license metadata | 1 |

**Result:** 1 finding(s) require triage.

## Non-allowlisted findings

| Package | Version | License (canonical) | License (raw) | Status | Used by |
|---|---|---|---|---|---|
| `xunit.abstractions` | 2.0.3 | *(unresolved)* |  | url-only | tests\NickERP.EdgeNode.Tests\NickERP.EdgeNode.Tests.csproj, tests\NickERP.Inspec... |

### Triage guidance

- **non-allowlisted-but-known**: review the license. If permissive (e.g. MPL-2.0 with weak copyleft on modified files), legal review can promote it to the allowlist with a rationale entry. If strong copyleft (GPL family), find a replacement package.
- **unresolved / not-restored**: run `dotnet restore` from repo root to populate the global packages folder, then re-run this script.
- **no-license-metadata**: the package's .nuspec has neither `<license>` nor `<licenseUrl>`. Check the package source / GitHub repo manually; capture the license in the alias_normalisations map of `license-allowlist.json`.
