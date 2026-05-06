# NickERP v2 license audit report

**Date (UTC):** 2026-05-06
**Tool:** `run-license-audit.ps1` + `dotnet list package --include-transitive`
**Allowlist:** `tools/security-scan/license-allowlist.json` (version 2026-05-06)
**Allowed licenses:** MIT, Apache-2.0, BSD-3-Clause, BSD-2-Clause, ISC, MS-PL, MS-EULA, PostgreSQL, Unlicense
**Unique (package, version) pairs:** 235

Reference: docs/security/audit-checklist-2026.md SEC-DEP-3.

---

## Summary

| Bucket | Count |
|---|---|
| Allowed | 229 |
| Non-allowlisted licenses | 1 |
| Unknown / missing license metadata | 5 |

**Result:** 6 finding(s) require triage.

## Non-allowlisted findings

| Package | Version | License (canonical) | License (raw) | Status | Used by |
|---|---|---|---|---|---|
| `FSharp.UMX` | 1.1.0 | *(unresolved)* |  | no-license-metadata | tests\NickERP.Perf.Tests\NickERP.Perf.Tests.csproj |
| `FuncyDown` | 1.4.2 | *(unresolved)* |  | url-only | tests\NickERP.Perf.Tests\NickERP.Perf.Tests.csproj |
| `HdrHistogram` | 2.5.0 | *(unresolved)* |  | url-only | tests\NickERP.Perf.Tests\NickERP.Perf.Tests.csproj |
| `NBomber` | 6.1.0 | LICENSE | LICENSE | ok | tests\NickERP.Perf.Tests\NickERP.Perf.Tests.csproj |
| `OneOf` | 3.0.163 | *(unresolved)* |  | url-only | tests\NickERP.Perf.Tests\NickERP.Perf.Tests.csproj |
| `xunit.abstractions` | 2.0.3 | *(unresolved)* |  | url-only | tests\NickERP.EdgeNode.Tests\NickERP.EdgeNode.Tests.csproj, tests\NickERP.Inspec... |

### Triage guidance

- **non-allowlisted-but-known**: review the license. If permissive (e.g. MPL-2.0 with weak copyleft on modified files), legal review can promote it to the allowlist with a rationale entry. If strong copyleft (GPL family), find a replacement package.
- **unresolved / not-restored**: run `dotnet restore` from repo root to populate the global packages folder, then re-run this script.
- **no-license-metadata**: the package's .nuspec has neither `<license>` nor `<licenseUrl>`. Check the package source / GitHub repo manually; capture the license in the alias_normalisations map of `license-allowlist.json`.

---

## Sprint 57 triage status (2026-05-06)

The 6 findings above are all triaged and have entries in
`tools/security-scan/license-allowlist.json` `package_license_overrides`
with rationale in `tools/security-scan/license-allowlist-rationale.md`.
Summary:

| Package | Triage outcome | Action |
|---|---|---|
| `FSharp.UMX` 1.1.0 | MIT (override) | Cleared by override map — see rationale §6 |
| `FuncyDown` 1.4.2 | MIT (override) | Cleared by override map — see rationale §3 |
| `HdrHistogram` 2.5.0 | BSD-2-Clause (override; dual with public-domain) | Cleared by override map — see rationale §4 |
| `OneOf` 3.0.163 | MIT (override) | Cleared by override map — see rationale §5 |
| `xunit.abstractions` 2.0.3 | Apache-2.0 (override) | Cleared by override map — see rationale §7 |
| **`NBomber` 6.1.0** | **PROPRIETARY — P0 finding** | **NOT cleared.** Operator decision required: purchase subscription / replace runner / drop perf harness. See rationale §2 + audit-checklist-2026 SEC-DEP-3. |

The previous 2026-05-06 (pre-Sprint-57) report flagged 9 entries
including 3 PostgreSQL-licensed Npgsql packages; those cleared in this
re-run because `PostgreSQL` was added to the `allowed[]` list. Net
delta: 9 → 6 flagged, 1 of which (`NBomber`) is the only un-resolvable
finding pending operator decision.

The 5 unresolved (`url-only` / `no-license-metadata`) findings stay in
the report until `run-license-audit.ps1` is extended (out-of-scope this
sprint — script owned by Sprint 52) to consume the
`package_license_overrides` map. Until that lands, these entries are
expected and benign — auditors reconcile each against the rationale
file in seconds.
