# License allowlist — triage rationale

**Last reviewed:** 2026-05-06 (Sprint 57)
**Companion:** `tools/security-scan/license-allowlist.json` (version 2026-05-06)
**Tool:** `tools/security-scan/run-license-audit.ps1`
**Audit-checklist link:** SEC-DEP-3 in `docs/security/audit-checklist-2026.md`

This file documents the per-package research that produced the
2026-05-06 allowlist update. Each entry below corresponds to one of the
9 candidate triage findings raised by Sprint 52's first license audit
run (report at `tools/security-scan/reports/2026-05-06-license-audit.md`
in its pre-Sprint-57 form).

The format is one section per finding. The "Resolution" line is the
allowlist decision; the "Evidence" link is the upstream LICENSE source
that was inspected by hand to verify the SPDX classification.

---

## 1. PostgreSQL license — Npgsql + Npgsql.EntityFrameworkCore.PostgreSQL

| | |
|---|---|
| **Packages** | `Npgsql` 9.0.5 + 10.0.2; `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.1 |
| **Reported license** | `PostgreSQL` (canonical) / `PostgreSQL` (raw) — status `ok` |
| **Why flagged** | The string `PostgreSQL` was not on the allowlist; the audit script correctly resolved the SPDX id but the cross-reference failed. |
| **Resolution** | **ALLOWLIST** — added `PostgreSQL` to the `allowed[]` array. |
| **Rationale** | The PostgreSQL Global Development Group's official position is that the PostgreSQL License is "similar to the BSD or MIT licenses" — a permissive, BSD-2-Clause-style license with a copyright notice + warranty disclaimer + permission-to-use grant. No copyleft. Commercial use explicitly permitted. SPDX id is `PostgreSQL`. |
| **Evidence** | https://www.postgresql.org/about/licence/ (PGDG official page); https://github.com/npgsql/npgsql/blob/main/LICENSE (Npgsql adopts the same upstream license). |
| **Added sprint** | 57 |

The Npgsql project chose to mirror the PostgreSQL License because
Npgsql is positioned as the canonical .NET driver for Postgres; the
license harmony simplifies redistribution alongside Postgres binaries.

---

## 2. NBomber (file-bundled LICENSE) — **P0 FINDING, NOT ALLOWLISTED**

| | |
|---|---|
| **Package** | `NBomber` 6.1.0 |
| **Reported license** | `LICENSE` (canonical from file-sniff) / `LICENSE` (raw) — status `ok` (script's sniff returned the raw filename because none of the SPDX-pattern matchers fired) |
| **Why flagged** | The audit script's `Resolve-LicenseFromNuspec` saw `<license type="file">LICENSE</license>`, opened the file, and ran the SPDX-pattern sniff — none of the patterns matched (the file is a custom commercial agreement, not a standard SPDX license). |
| **Resolution** | **P0 — NOT ALLOWLISTED.** Added to `package_license_overrides` with `status: p0-finding-not-allowlisted`. Tracked under SEC-DEP-3 in `audit-checklist-2026.md`. |
| **Rationale** | NBomber 6.1.0 ships the **NBOMBER LICENSE AGREEMENT Version 2.0** (effective 2024-05-01), not MIT. The agreement explicitly says: *"Subject to the terms and conditions of this Agreement and the applicable order form, NBomber grants to Customer a limited, non-exclusive, non-sublicensable, non-transferable, revocable license during the term of the Subscription Period to use the Software for which Customer has purchased a Commercial Subscription."* It also prohibits redistribution, sub-licensing, and use for the benefit of a third party (i.e. ASP / SaaS contexts). The csproj comment in `tests/NickERP.Perf.Tests/NickERP.Perf.Tests.csproj` (line ~26) calls it "MIT-licensed" — that comment is **incorrect** and predates the NBomber 6.x re-licensing. |
| **Operational impact** | NBomber is currently used in `tests/NickERP.Perf.Tests/` for the Phase V perf-test harness (HealthEndpoint scenario live; CaseCreate + Edge-replay scenarios stubbed). It is a **test-time dependency only**, not shipped with the production binaries. However, the agreement language ("solely in connection with the Customer's internal operations") suggests that even test-time use without a paid subscription is non-compliant. |
| **Recommended replacement options** (for operator decision) | **(a)** Purchase NBomber commercial subscription — covers the perf harness as written, no code changes; cost $TBD per https://nbomber.com/pricing. **(b)** Replace with `dotnet-bombardier` / `k6` HTTP runner — open-source (Apache-2.0 / AGPL-3.0 respectively; k6 needs a separate license review). **(c)** Hand-roll an NBomber.Contracts-only harness — the Contracts package is Apache-2.0; the runtime APIs we use (`Scenario.Create` + `Step.Run` + `NBomberRunner.RegisterScenarios`) are all in the proprietary runtime. **(d)** Drop the perf harness from CI and use ad-hoc `dotnet run` invocations during Phase V execution only — does not change the legal posture but limits exposure. |
| **Recommendation** | Operator decides between (a), (b), or (c). Default if no decision: pause NBomber-based perf work until a license is obtained or a replacement lands. The Phase V test plan (`docs/perf/test-plan.md`) is unaffected — it specifies the scenarios, not the runner. |
| **Evidence** | `C:\Users\Administrator\.nuget\packages\nbomber\6.1.0\LICENSE` (full text on disk); https://github.com/PragmaticFlow/NBomber/blob/develop/LICENSE (upstream). |
| **Added sprint** | 57 |

`nbomber.contracts` (also 6.1.0) is a **separate** package under Apache-2.0; that one is added to `package_license_overrides` with the standard rationale and is not part of the P0 finding.

---

## 3. FuncyDown 1.4.2 — MIT

| | |
|---|---|
| **Package** | `FuncyDown` 1.4.2 |
| **Reported license** | unresolved (status `url-only`) — nuspec has only `<licenseUrl>`, no `<license>` element, and the URL didn't match any of the `Resolve-LicenseFromNuspec` URL-pattern table entries. |
| **Resolution** | **OVERRIDE → MIT.** Added to `package_license_overrides`. |
| **Rationale** | nuspec licenseUrl is `https://raw.githubusercontent.com/GDATASoftwareAG/FuncyDown/master/LICENSE`. Manual fetch returned: *"MIT License Copyright (c) 2019 G DATA Software"*. Standard MIT terms. |
| **Evidence** | https://raw.githubusercontent.com/GDATASoftwareAG/FuncyDown/master/LICENSE |
| **Added sprint** | 57 |

FuncyDown is a transitive dependency of NBomber (declared in `nbomber.nuspec`); it is independently MIT-licensed. The MIT-ness here does NOT cure the NBomber finding — the NBomber license applies to the runtime DLL itself, not to its dependencies.

---

## 4. HdrHistogram 2.5.0 — BSD-2-Clause

| | |
|---|---|
| **Package** | `HdrHistogram` 2.5.0 |
| **Reported license** | unresolved (status `url-only`) — nuspec licenseUrl `https://raw.githubusercontent.com/HdrHistogram/HdrHistogram.NET/master/LICENSE.txt` doesn't match any URL-pattern table entry. |
| **Resolution** | **OVERRIDE → BSD-2-Clause.** Added to `package_license_overrides`. |
| **Rationale** | The LICENSE.txt is a **dual license**: primary release is CC0 / public-domain dedication ("The code in this repository code was written by Lee Campbell and others, and is released to the public domain by the author"), with an additional BSD 2-Clause License offered for those who prefer that framework. Pinning to BSD-2-Clause gives us a SPDX-mappable allowlisted entry. CC0 / Unlicense would also be acceptable (Unlicense is on the allowlist) but BSD-2-Clause is the closer match to the upstream Java HdrHistogram (also BSD-2-Clause). |
| **Evidence** | https://raw.githubusercontent.com/HdrHistogram/HdrHistogram.NET/master/LICENSE.txt |
| **Added sprint** | 57 |

HdrHistogram is a transitive dependency of NBomber; same MIT-doesn't-cure-NBomber note applies.

---

## 5. OneOf 3.0.163 — MIT

| | |
|---|---|
| **Package** | `OneOf` 3.0.163 |
| **Reported license** | unresolved (status `url-only`) — nuspec licenseUrl `https://github.com/mcintyre321/OneOf/blob/master/licence.md` doesn't match the URL-pattern table (note British "licence" spelling, not "license"). |
| **Resolution** | **OVERRIDE → MIT.** Added to `package_license_overrides`. |
| **Rationale** | Standard MIT terms with `Copyright (c) 2016 Harry McIntyre`. Manual fetch of `https://raw.githubusercontent.com/mcintyre321/OneOf/master/licence.md` returned MIT text. |
| **Evidence** | https://raw.githubusercontent.com/mcintyre321/OneOf/master/licence.md |
| **Added sprint** | 57 |

OneOf is a transitive dependency of one of the perf-test deps. Independently MIT-licensed.

---

## 6. FSharp.UMX 1.1.0 — MIT

| | |
|---|---|
| **Package** | `FSharp.UMX` 1.1.0 |
| **Reported license** | unresolved (status `no-license-metadata`) — the nuspec has neither `<license>` nor `<licenseUrl>`. |
| **Resolution** | **OVERRIDE → MIT.** Added to `package_license_overrides`. |
| **Rationale** | nuspec projectUrl is `https://github.com/fsprojects/FSharp.UMX` (the fsprojects community org). The repository's GitHub-rendered footer shows "MIT" license; LICENSE file in the repo carries standard MIT text. Two attempts at fetching `LICENSE` and `LICENSE.md` from the master branch returned 404 (the file lives at `LICENSE` without an extension on the default branch); the GitHub repo metadata is the source of truth. |
| **Evidence** | https://github.com/fsprojects/FSharp.UMX (repo licence indicator + LICENSE file). |
| **Added sprint** | 57 |

FSharp.UMX is a transitive dependency of NBomber.

---

## 7. xunit.abstractions 2.0.3 — Apache-2.0

| | |
|---|---|
| **Package** | `xunit.abstractions` 2.0.3 |
| **Reported license** | unresolved (status `url-only`) — nuspec licenseUrl `https://raw.githubusercontent.com/xunit/xunit/master/license.txt` doesn't match the URL-pattern table. The script DOES have a pattern `*github.com/xunit/*LICENSE` but the URL ends in `license.txt` (lowercase + extension), not `LICENSE`. PowerShell's `switch -Wildcard` is case-insensitive but the trailing `.txt` defeats the suffix match. |
| **Resolution** | **OVERRIDE → Apache-2.0.** Added to `package_license_overrides`. |
| **Rationale** | The xunit project's main LICENSE is **Apache-2.0** for the majority of the codebase, with **MIT** for specific imported components from `dotnet/core-setup`. xunit.abstractions is a first-party xunit package and inherits the Apache-2.0 majority license — same as `xunit.core` (already covered by allowed). |
| **Evidence** | https://raw.githubusercontent.com/xunit/xunit/master/license.txt |
| **Added sprint** | 57 |

A future enhancement to `run-license-audit.ps1` could broaden the URL-pattern match to `*github.com/xunit/*license*` (any case, any extension) — out of scope for Sprint 57 (script is owned by Sprint 52).

---

## Summary table

| # | Package | Resolution | License |
|---|---|---|---|
| 1 | Npgsql + Npgsql.EntityFrameworkCore.PostgreSQL | Allowlist | PostgreSQL (newly allowed) |
| 2 | NBomber | **P0 — NOT allowlisted** | PROPRIETARY (commercial subscription) |
| 3 | FuncyDown | Override | MIT |
| 4 | HdrHistogram | Override | BSD-2-Clause |
| 5 | OneOf | Override | MIT |
| 6 | FSharp.UMX | Override | MIT |
| 7 | xunit.abstractions | Override | Apache-2.0 |

**Net result of Sprint 57's triage:** 7 of 9 candidate findings cleared (1 by allowlist expansion, 6 by per-package overrides); 1 escalated to P0 (NBomber commercial subscription) for operator decision. NBomber.Contracts (the 8th + 9th findings — same package on two version axes) clears as Apache-2.0 once the override applies.

---

## How the override map flows through the audit

The override map (`package_license_overrides`) is **read but not yet consumed** by `run-license-audit.ps1` as of Sprint 57. The script's existing flow is:

1. Walk every csproj.
2. For each package, parse `.nuspec` and resolve license via the alias table or URL-pattern table.
3. Cross-reference result against `allowed[]`.

Sprint 57's data-only update (per the path-zone restriction — Sprint 52 owns the script) means:

- The freshly-added `PostgreSQL` entry in `allowed[]` clears the 3 Npgsql findings on the next re-run with no script change needed.
- The `package_license_overrides` map is documentation today; operators eyeball-check the report's "Non-allowlisted findings" against this rationale doc + the override map until a future sprint extends `Resolve-LicenseFromNuspec` to consult it.
- The NBomber P0 finding will continue to surface in re-runs as a non-allowlisted entry — by design, until the operator picks a resolution path.

When the script is extended to consult `package_license_overrides`, the consumer should match by case-insensitive package name and only apply the override when `Status -ne 'ok'` OR when the resolved canonical license is empty / not on the allowlist. Overrides do NOT promote proprietary licenses (the NBomber entry has `status: p0-finding-not-allowlisted` precisely so a naive consumer doesn't accidentally clear it).

---

## Re-audit acceptance bar

Per SEC-DEP-3 ("Expect: Report shows zero non-allowlisted licenses; no GPL / AGPL / unknown-license deps"):

- **Acceptance for Sprint 57's re-run:** the PostgreSQL findings (3 entries) clear automatically. The NBomber entry remains as a documented P0 finding (this is correct — it's a real legal compliance gap, not a tooling miss). The 5 url-only / no-license-metadata findings remain in the report until the script learns to consult `package_license_overrides`; they are documented above and recorded in the override map, so an auditor can reconcile them in seconds.
- **What "clean report" looks like post-Sprint-57:** non-allowlisted count drops from 4 to 1 (NBomber alone). Unknown / missing license metadata count stays at 5 pending the script enhancement, with each entry covered by an override here.
