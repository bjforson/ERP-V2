# Phase V audit dry-run plan

Execute `docs/security/audit-checklist-2026.md` on the locked pilot site before runbook 14 §11 (real-traffic cutover).

## Goal

Walk every SEC-* item; resolve all P0 + P1; ticket P2 + P3. Exit per checklist "Phase V exit criteria": P0 all pass, P1 pass-or-ticketed, registers countersigned, edge keys issued, restore drill done.

Adversarial-by-default: auditor must NOT have implemented the code under review (rb14 §10.1).

## Inventory

`docs/security/audit-checklist-2026.md` ships **~90 SEC-* items** across 11 categories:

| Cat | Items | Area |
|---|---|---|
| AUTH | 8 | CF Access JWT, dev-bypass, claims, edge HMAC |
| AUTHZ | 7 | `[Authorize]` default, scope, antiforgery, file gates |
| TENANT | 13 | RLS forced, system-context register, soft-delete |
| SECRETS | 8 | env posture, role separation, cipher pass |
| TLS | 7 | HTTPS-only, HSTS, version, SMTP, DB, edge |
| DB | 10 | PG17, hba, grants, pgbackrest, drill, standby |
| AUDIT | 8 | non-bypassable writes, retention, PII, correlation |
| EDGE | 7 | HMAC rotation, SQLite buffer, replay allowlist |
| MOD | 10 | inspection / nickfinance / nickhr |
| DEP | 6 | vuln scan, outdated, license, MailKit, .NET, Npgsql |
| HEAD | 6 | CSP, X-CTO, X-Frame, Referrer, cookies, Permissions |

Severity counts (from `**Severity:**` lines):

| Severity | Count | Meaning |
|---|---|---|
| P0 | ~43 | Block pilot |
| P1 | ~36 | Fix-before-launch |
| P2 | ~10 | Fix by launch+1mo |
| P3 | 2 | Backlog |

Open known finding: **SEC-DEP-3 NBomber license** (Sprint 57 triage; operator-decision: purchase / replace / drop perf harness). PLAN.md §22.7 flags it.

## Sequence

Left-to-right surfaces fast failures early; each band parallelisable internally:

1. **Posture-gates** — DEP, HEAD, TLS. ~19 items. ~1d. If TLS-1 / DEP-1 fails, downstream is conditional.
2. **Identity + secrets** — AUTH, SECRETS. ~16 items. ~2d. CF Access + secret posture must hold before tenancy is testable.
3. **Tenancy core** — TENANT, AUTHZ. ~20 items. ~3d. RLS-forced + system-context register + `[Authorize]` default; cross-check `docs/system-context-audit-register.md`.
4. **Data plane** — DB, AUDIT. ~18 items. ~2d. PG17 lock, pgbackrest (covered by HA checklist), audit retention + correlation.
5. **Edge + module** — EDGE, MOD. ~17 items. ~2d. Per-edge HMAC, buffer protection, module gates.

Reorder only on hard-block (e.g. DEP-1 finds a critical CVE in a top-level package → stop, patch, restart).

## Time budget

Methodology: P0 ~2h each (verify + reproduce + file + draft fix), P1 ~1h, P2 ~0.5h, P3 negligible.

```
P0:  43 × 2h   = 86h
P1:  36 × 1h   = 36h
P2:  10 × 0.5h = 5h
P3:  2 × 0.25h = 0.5h
Total          ~127h
```

1 auditor at ~30h/week = **~4.2 weeks**. 2 parallel auditors on non-overlapping bands = **~2.5 weeks**. Add ~1 week buffer for fix-forward iterations on P0 findings.

Excludes perf load test (rb14 §10.2) and restore drill (§10.3). All three together = §10.4 Phase V exit gate.

## Roles

- **Engineer (auditor)** — NOT implementer of code under review (rb14 §10.1). Runs every `verify`; files `AUD-{n}` with severity + evidence.
- **Engineer (fixer)** — lands fixes on `phase-v/` branch; re-runs failing verify. Different from auditor where possible.
- **Operator** — owns SECRETS-3..7, DB-2 (pg_hba), DB-4..6 (already covered by `ha-provisioning-checklist.md`), TLS-1..7.
- **External auditor (optional)** — adversarial review of findings before sign-off. Reduces blindspot risk on first pilot.

## Tooling

Shipped (PLAN.md §22.7):

- **secret-scan** — `tools/security-scan/` covers SEC-SECRETS-8 (Sprint 39).
- **license-audit** + **license-allowlist-rationale.md** — covers SEC-DEP-3 (Sprint 39 + 57).
- **trufflehog** — verified-finding detection for SEC-SECRETS-8.
- **NBomber harness** — `tests/NickERP.Perf.Tests/` (Sprint 30 + 55). License is the open SEC-DEP-3 decision.
- **MultiTenantInvariantProbe** (Sprint 43) — runtime check for SEC-TENANT-3 / -7 / -9; run during smoke per rb14 §8.

Manual-only: pg_hba (DB-2), TLS termination (TLS-1..3), CSP/headers (HEAD-*), edge SQLite mode (EDGE-2). External vuln scanner (Snyk / dependabot / OSS) for SEC-DEP-1.

## Resolution gate

A finding closes when one of:

1. **Test added** that would catch the regression (preferred for AUTH / TENANT / AUTHZ — runtime-verifiable invariants).
2. **Fix landed** + verify re-runs green; commit SHA captured in `AUD-{n}`.
3. **Runbook entry** added when the gap is operator-procedure, not code.
4. **Accepted-risk doc** for findings the team can't reasonably fix pre-pilot — requires written justification + business-owner + tech-lead signature. SEC-DEP-3 is the template.

A closed P0 must use option 1 or 2; documentation alone does not close a P0.

## Stop conditions

Halt + replan when:

- **Systemic failure pattern** — same root cause across ≥ 3 categories (e.g. RLS not actually forced anywhere → re-baseline platform invariants).
- **Tooling gap blocks > 5 items** — e.g. CF Access JWKS unreachable → fix tooling, restart band.
- **P0 blowout** — > 50% of P0 items fail → platform not pilot-ready; escalate to business owner.
- **Site-specific surprise** — site posture invalidates checklist assumptions → return to rb14 §3.

Per rb14 §3.4: the audit is the **informed guess**; the 14-day soak (§11.4) is truth. A passing audit doesn't prove the pilot works — it just removes the disqualifying ways it could fail.
