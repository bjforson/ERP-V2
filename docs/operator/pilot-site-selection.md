# Pilot site selection — decision memo

**Decision needed.** Lock pilot site (Kotoka Cargo / KIA vs Takoradi). Pilot launch (runbook 14) blocked until named.

**Stakeholders.**
- **Decides:** business owner + customs-side §2.4 MOU counterpart.
- **Affected:** v2 dev team (deploy support); operator team (HA + edge stand-up); customs operators at chosen site.
- **Sign-off:** §2.4 counterpart countersigns the §3.5 one-pager.

---

## Source-of-truth gap

PLAN.md §13 is the Sprint 1 status snapshot, not the site matrix. Decision framework lives in **plan-file §13** at `~/.claude/plans/tingly-launching-quasar.md` (external). Mechanical scoring procedure lives in `docs/runbooks/14-pilot-site-standup.md` §3.

This memo is **what we know (framework) / what we need to find out (site facts)**. Do not invent operational facts about either site — fill TBDs from operator + customs walk-throughs.

---

## Decision criteria (runbook 14 §3)

**Hard gates** — fail any → site is out, regardless of score (§3.1):

1. Site has ≥ 1 functional scanner with a vendor-neutral plugin (today: FS6000 or ASE; mock is dev-only, does not qualify).
2. Connectivity ≥ 95% uptime over §2.5 measurement window.
3. ≥ 1 named customs operator willing to participate (in MOU).
4. Customs-cooperation MOU signed for this site.

**Weighted scoring** (§3.2 — locked; do not invent criteria):

| Criterion | Pilot prefers | Weight |
|---|---|---|
| Traffic volume | Lower (smaller blast radius) | 3 |
| Connectivity reliability | Higher | 3 |
| Local IT support presence | Higher | 2 |
| Operator cooperation | Higher | 2 |
| Scanner availability + condition | Higher | 3 |
| Geographical accessibility for v2 team | Higher | 1 |
| Operational simplicity | Higher | 2 |
| Low political / contractual risk | Higher | 2 |

Pilot bias: **lowest-traffic gate-passer wins** (§3.3). First pilot exists to catch surprises pre-pilot tests missed; high traffic amplifies any surprise into a customer-visible incident.

---

## Site profile: Kotoka Cargo (KIA)

- **Strengths:** TBD — operator input needed on contractor cooperation, scanner fleet condition, IT footprint.
- **Weaknesses:** TBD — air cargo throughput likely higher than Takoradi (raises blast radius).
- **Risks:** TBD — political / contractual posture at airport-zone customs.
- **Dependencies:** TBD — named operators willing to be the §2.4 MOU counterpart.

## Site profile: Takoradi

- **Strengths:** TBD — likely lower-traffic seaport (favours blast-radius criterion).
- **Weaknesses:** TBD — port connectivity reliability (needs §2.5 measurement data).
- **Risks:** TBD — geo accessibility for v2 team (Accra → Takoradi travel cost).
- **Dependencies:** TBD — scanner inventory + adapter coverage (FS6000 / ASE / other).

---

## Side-by-side matrix (operator fills)

| Criterion (weight) | Kotoka score (1-5) | Kotoka weighted | Takoradi score (1-5) | Takoradi weighted |
|---|---|---|---|---|
| Traffic (×3) | TBD | — | TBD | — |
| Connectivity (×3) | TBD | — | TBD | — |
| IT support (×2) | TBD | — | TBD | — |
| Operator coop (×2) | TBD | — | TBD | — |
| Scanner avail. (×3) | TBD | — | TBD | — |
| Geo accessibility (×1) | TBD | — | TBD | — |
| Op simplicity (×2) | TBD | — | TBD | — |
| Low political risk (×2) | TBD | — | TBD | — |
| **Total** | — | **TBD** | — | **TBD** |

Score 1 (worst) to 5 (best). "Pilot prefers higher" criterion scores 5 when site is best on that axis.

---

## Recommendation

**TBD** — data-bounded; cannot be made from inside the codebase. Fill the matrix, then apply §3.3 lowest-traffic-gate-passer rule: if both sites pass all four hard gates, lower-traffic site wins tiebreak.

Trade-offs the recommendation must name:
- If Kotoka wins on cooperation but Takoradi wins on traffic, **traffic wins** (§3.3).
- If a site fails any hard gate, weighted score is irrelevant (§3.1).

---

## Decision triggers (would flip recommendation)

- Hard-gate failure late: MOU counterpart withdraws / scanner adapter coverage wrong → site out, other wins by default.
- §2.5 connectivity dips below 95% → fails gate 2.
- Customs leadership rotation invalidates MOU.

---

## Go-no-go checklist (before runbook 14 can execute)

- [ ] Site named in writing; §3.5 one-pager committed (`pilots/{site}/site-selection-{date}.md`).
- [ ] All four hard gates documented as Pass with evidence (§3.1).
- [ ] Weighted scoring sheet signed by customs-side counterpart (§3.5).
- [ ] §2.4 MOU signed for chosen site, names customs operator.
- [ ] §2.5 connectivity baseline ≥ 95% over measurement window.
- [ ] Scanner inventory lists ≥ 1 unit with FS6000 / ASE coverage.
- [ ] §4.4 sizing review submitted to v2 dev team for sign-off.
- [ ] Sign-off captured: business owner + §2.4 counterpart within single 24h window.

When all rows ticked, runbook 14 §1 (pilot stand-up) can begin.
