# Vendor call results — April 2026

**Calls completed:** 2026-04-29.
**Companion runbook:** [`vendor-call-2026-04.md`](vendor-call-2026-04.md).
**Roadmap doc updated:** [`IMAGE-ANALYSIS-MODERNIZATION.md`](../IMAGE-ANALYSIS-MODERNIZATION.md) — §5.6 Q-C1, §6.7.8 Q-J1, §6.11.15 Q-N1, and §6.11.3 (ICUMS mode lock).

---

## Call 1 — FS6000 supplier
**Date / time:** 2026-04-29
**Spoke with:** TBD (capture name + email + role here)
**Firmware version on our unit:** TBD (capture during the email follow-up)

### Q-C1 (DICOS export)
**Answer: NO.** The current FS6000 firmware does not expose a DICOS / NEMA IIC 1 export option, paid or otherwise. Consistent with our planning assumption — §5.5 already locked posture as **design-ready, deploy-deferred** and §5.5 item 5 (the "switch trigger" at ≥ 30 % fleet DICOS support) sits dormant. No project plan change required. The `IScannerAdapter.SupportsDicosExport` capability flag stays in the contract for future hardware; no FS6000 adapter ever sets it true.

### Q-J1 (side-view output)
**Answer: NO.** The FS6000 produces a single top-down view only; no side-view channel is emitted. Consistent with our planning assumption. §6.7 stays in **design-ready, deploy-deferred** status. The `ScannerCapabilities.SupportsDualView` flag and `DualViewGeometry` record stay in the contract for future hardware. Q-J2 (whether v1 decoders silently drop a side-view we missed) is closed as moot — there is nothing to drop.

### Follow-ups owed by the vendor
- [ ] Email confirming firmware version on our unit (write into the heading above when received)
- [ ] No spec sheet requested — both answers are NO and well-understood

---

## Call 2 — ICUMS Ghana
**Date / time:** 2026-04-29
**Spoke with:** TBD (capture name + email + role here)

### Q-N1 (date-range vs per-declaration)
**Answer: YES, with both modes available.** The ICUMS API supports **batch fetch by date range** (decided-at window) AND per-declaration lookups by BOE / container number. Webhooks were not offered. This is the best practical outcome we could have hoped for: once-daily pull mode is sufficient; we do not need to fall back to per-declaration polling at scale.

**Project effect.** §6.11.3 has been updated to lock the first ICUMS Ghana `ExternalSystemInstance` into **pull mode at v1's production cadence — every 30 minutes** (cloned from `IcumPipelineOrchestratorService.BatchIntervalMinutes = 30` in v1's `NickScanCentralImagingPortal.API/appsettings.json:282`, not invented). Per-declaration lookup is retained as the reconciliation-fallback path for unmatched cases. Hybrid mode stays in the contract for future authorities offering webhooks.

### Follow-ups owed by the vendor
- [ ] OpenAPI / Swagger documentation by email — needed to nail down the exact endpoint path, the date-field name (decided-at vs last-modified-at vs received-at), maximum window size, and rate limits. Expected by end of next week.
- [ ] Engineer's full name + role + direct email — capture into the heading above. This is the liaison for the next several months of v2 inbound-adapter work.

---

## What changes in the project

| Effect | Detail |
|---|---|
| §5 DICOS readiness | No change. Posture stays "design-ready, deploy-deferred." Q-C1 closed. |
| §6.7 dual-view registration | No change. Section stays deferred. Q-J1 closed; Q-J2 closed as moot. |
| §6.11 inbound post-hoc adapter | **Simplified.** Pull-mode-only for ICUMS; no hybrid mode needed for the first authority. Per-declaration lookup retained as reconciliation fallback. Q-N1 closed. |
| Phase 7.0 contract freeze | `SupportsDicosExport` and `SupportsDualView` + `DualViewGeometry` capability flags stay in the contract for future hardware — they're additive and cheap. No removal. |
| Cross-cutting open questions §7.1 | Three "vendor-contact" rows close. Q-N1 follow-ups (API docs by email) tracked under Phase 7.1 onboarding work. |

---

## What still needs follow-up

- The two TBD entries above (vendor contact names / emails / roles + firmware version) — fill in once the email replies land.
- The ICUMS API documentation arrival (5-day calendar reminder per the runbook). If not received within 10 working days, escalate to whoever owns the ICUMS commercial relationship.
- No technical-side blocker remains; the §6.11 adapter spec is implementation-ready for the pull-mode path.
