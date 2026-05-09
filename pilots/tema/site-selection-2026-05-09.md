# Pilot site selection — Tema (2026-05-09)

> **Output document for `docs/runbooks/14-pilot-site-standup.md` §3.5.**
> Captures the hard-gate evidence, weighted scoring, decision, and sign-off for the Tema pilot.

**Site chosen:** Tema
**Decision date:** 2026-05-09
**Tagged release:** [`pilot-tema-2026-05-09`](https://github.com/bjforson/ERP-V2/releases/tag/pilot-tema-2026-05-09) (pre-pilot saturation 49/27-41 sprints)

---

## §3.1 Hard gates

| # | Gate | Result | Evidence |
|---|------|--------|----------|
| 1 | Scanner with vendor-neutral plugin | **PASS** (caveat) | 1 working ASE unit on-site, recent maintenance. ⚠ ASE plugin is currently stub-only (real-protocol wiring is a known post-pilot TODO at `modules/inspection/plugins/NickERP.Inspection.Scanners.Ase/AseScannerAdapter.cs:88,231`). Pilot operates in stub-mode for §11 cutover; real-protocol completion is a Phase 6.x deliverable. |
| 2 | Connectivity ≥95% uptime | **PASS** | Baseline measured at ≥95% over §2.5 measurement window. _TBD: measurement period + tool reference._ |
| 3 | Named customs operator in MOU | **WAIVED** | v2 dev team acts as the on-site operator for first pilot. Individual-naming requirement does not apply to v2-team-operated mode. Justified per §3.6 of this memo. |
| 4 | Org-level cooperation in writing | **PASS** | Org-level MOU signed within the last 30 days between v2 dev org and Tema's institutional owner. _TBD: exact date + parties._ |

**Gate 3 waiver justification:** First pilot is run in v2-team-operated mode rather than customs-operator-staffed mode. The customs-side cooperation is institutional (gate 4) rather than individual (gate 3). This is appropriate for a first-pilot blast-radius posture: v2 team owns incident response directly without escalation through a customs operator chain. Subsequent sites are expected to revert to gate-3-staffed mode.

**Gate-pass result:** Tema passes all four gates (3 PASS + 1 WAIVED). Proceed to §3.2 weighted scoring.

---

## §3.2 Weighted scoring

| Criterion | Score (1-5) | Weight | Subtotal | Note |
|-----------|-------------|--------|----------|------|
| Traffic volume (lower = better) | **3** (medium) | ×3 | 9 | Medium throughput; not lowest-blast-radius option but workable. |
| Connectivity reliability | **5** (excellent / redundant) | ×3 | 15 | Multiple paths, near-100% uptime. |
| Local IT support presence | **5** (dedicated on-site IT) | ×2 | 10 | Full-time IT presence, owns hardware/network/login. |
| Operator cooperation | **5** (enthusiastic) | ×2 | 10 | Tema institution actively wants the pilot. |
| Scanner availability + condition | **3** (single ASE, working) | ×3 | 9 | One ASE unit; meets gate but no redundancy. |
| Geographical accessibility (v2 team) | **5** (same-city / sub-1h drive) | ×1 | 5 | v2 team can be on-site within an hour. |
| Operational simplicity | **2** (many edge cases) | ×2 | 4 | Multi-shift operations — handover + tenant-context complexity. |
| Low political / contractual risk | **2** (high risk) ⚠ | ×2 | 4 | Operator-recorded rationale: _"same operator already has a 20-year contract."_ See **flag** below — score may need revision. |
| | | | **66** | |

> **Flag — political-risk score may be inverted.** A 20-year operator contract is generally a **low-risk** indicator (stability, no transition pressure), which would map to score 5 (×2 = 10), not 2 (×2 = 4). If the recorded score reflects the 20-year contract as a *negative* (lock-in / inability to pivot), keep at 2. If it reflects stability, revise to 5 — total becomes 72. **Resolve before sign-off.**

**No competing site evaluated.** Per the operator's lock decision, Tema was selected without head-to-head scoring against Kotoka or Takoradi. The weighted score documents Tema's standalone profile for §10 audit traceability rather than a comparative ranking. Cross-reference: original two-candidate evaluation in `docs/operator/pilot-site-selection.md`.

---

## §3.3 Lowest-traffic-tiebreak applicability

Not applicable — single-candidate selection. The §3.3 rule (lowest-traffic gate-passer wins ties) only applies when multiple sites pass §3.1 + score within tiebreak range. Tema's traffic score (3 / medium) is recorded for audit completeness; if a second site is later evaluated head-to-head, this score becomes the comparison datum.

---

## §3.4 Runtime-gate qualifier

The real qualifier for Tema is the five gates passing for 14 days at runbook 14 §11.4 (real-traffic cutover sign-off). This memo captures the **informed guess** that Tema will satisfy §11.4; the §11.4 dashboard is the truth.

If §11.4 sign-off fails after 14 days at Tema, restart at §3 with a different site per §3.4. Do not patch a failing pilot site indefinitely.

---

## Output of §3.5 (this document)

- ✅ Hard-gate result for Tema (3 PASS + 1 WAIVED)
- ✅ Weighted score table (total 66; flag on criterion 8)
- ✅ Chosen site named (Tema)
- ⏳ Sign-off block (below — pending signatures)

---

## Sign-off

> Per runbook 14 §3.5, this document requires sign-off from the customs-side §2.4 counterpart (here: Tema's institutional counterpart, since gate 3 is waived) and the v2 business owner / decision-maker.

| Role | Name | Title / Org | Signature | Date |
|------|------|-------------|-----------|------|
| Decision-maker | _TBD_ | Product / PM (v2 dev org) | _signed_ | 2026-05-09 |
| Tema counterpart | _TBD_ | Site / Facility Manager (Tema institutional owner) | _signed_ | _TBD_ |
| v2 dev team rep (operator-of-record per gate 3 waiver) | _TBD_ | v2 dev team | _signed_ | _TBD_ |

**Action required before §4 hardware provisioning starts:**
1. Resolve political-risk score flag above (decide: 2 = lock-in risk, or 5 = stability — affects total).
2. Fill TBD names + titles in the sign-off table.
3. Capture wet or digital signatures (or signed-by-commit) before runbook 14 §4 begins.
4. Resolve TBD evidence:
   - Connectivity measurement period + tool (gate 2)
   - MOU signing date (gate 4)

Once all rows ticked, attach this document to runbook 14 §10 Phase V execution log (per §3.5 audit-trail requirement).

---

## Cross-references

- Runbook: [`docs/runbooks/14-pilot-site-standup.md`](../../docs/runbooks/14-pilot-site-standup.md) §3.1, §3.2, §3.3, §3.4, §3.5, §3.6
- Operator memo: [`docs/operator/pilot-site-selection.md`](../../docs/operator/pilot-site-selection.md) — original two-candidate (Kotoka vs Takoradi) decision framework
- Phase V audit-execution plan: [`docs/operator/phase-v-audit-dryrun.md`](../../docs/operator/phase-v-audit-dryrun.md)
- HA provisioning checklist: [`docs/operator/ha-provisioning-checklist.md`](../../docs/operator/ha-provisioning-checklist.md)
- Tagged release: [`pilot-tema-2026-05-09`](https://github.com/bjforson/ERP-V2/releases/tag/pilot-tema-2026-05-09)
