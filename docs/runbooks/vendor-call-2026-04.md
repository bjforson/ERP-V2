# Vendor call script — April 2026

**What this is.** A phone-call cheat-sheet for two short calls that unblock v2 design decisions. The caller does not need to be technical — every question is written to read aloud word-for-word.

**When to use.** As soon as both vendor contacts are reachable. The calls are independent; do them in either order.

**Who to call.**
- **Call 1:** the FS6000 supplier — the contact who handled the last firmware update or warranty conversation. Office manager has the card.
- **Call 2:** the ICUMS Ghana technical contact — the engineer (not the customs officer) who set up the current data feed. Ask the front desk to route to engineering rather than commercial.

**Expected duration.** ~10–15 min per call, ~30 min total. If a vendor wants to dive deep, ask for an email follow-up rather than holding the line.

**Important.** These are **information-gathering** calls. Do **not** agree to a purchase, contract change, pilot, or date. If pushed, say: *"I'm just collecting information today; I'll loop in the right person and someone will follow up by email."*

---

## Pre-call checklist

1. **One recent declaration number** — any container processed in the last week. Portal *Cases → Recent*; format `C 123456 26`. You will read it to ICUMS.
2. **The FS6000 scanner serial number** — silver plate on the gantry, or portal *Scanners → Demo FS6000 → Details*.
3. **Current FS6000 firmware version** if known (same Scanners page, field *Firmware*). If blank, ask the vendor to tell you.
4. **A blank notebook** with three headings: *Q-C1*, *Q-J1*, *Q-N1*.
5. **Authority to commit?** No. If asked for a number or date: *"I'll check with management and follow up by email."* Write down what they asked for.

---

## Conversation 1 — FS6000 supplier

Two questions: Q-C1 (DICOS export) and Q-J1 (side-view output). Both ask what the scanner already does, not about adding features.

### Q-C1 — Does the FS6000 export DICOS?

Roadmap link: [§5 DICOS readiness](../IMAGE-ANALYSIS-MODERNIZATION.md#5-dicos-readiness-assessment) — question Q-C1 in §5.6.

**Ask exactly:**

> *"On our current FS6000 firmware, is there an option to export scans in the DICOS format — that's the security-imaging standard, sometimes called NEMA IIC 1? We're not asking you to add it; we just want to know whether today's units already support it, even if it's an extra-cost licence."*

- **YES (available, possibly licensed).** Best outcome — we can plan to receive DICOS files directly. **Write down:** firmware version that added it, paid licence yes/no and rough price band, DICOS flavor (Cargo2D / CargoCT / TDR / ATR), contact email for a spec sheet. **Next:** email the spec-sheet request (template below).
- **YES, BUT (newer firmware / hardware add-on / TSA-only).** Useful. **Write down:** the exact condition and what it would cost. **Next:** email asking for the condition in writing. Do not commit on the call.
- **NO (proprietary only).** Expected. Roadmap already treats DICOS as design-ready, deploy-deferred — not a blocker. **Write down:** is DICOS on their roadmap at all? Rough timeline? **Next:** none; v2 plan continues as-is.
- **"Let me get back to you".** **Write down:** the name and email of whoever they will hand it to. **Next:** send the written follow-up the same day. Calendar reminder to chase in 5 working days.

**Always write down:** the firmware version they say we are on. We need it for Q-J1 too.

### Q-J1 — Does the FS6000 produce a side-view image?

Roadmap link: [§6.7 Dual-view registration](../IMAGE-ANALYSIS-MODERNIZATION.md#67-dual-view-registration) — question Q-J1 in §6.7.8.

**Ask exactly:**

> *"Do our current FS6000 units produce a separate side-view image alongside the main top-down image? I'm asking whether the hardware emits two views — top and side — or just one. The vendor documentation we have is ambiguous, so we'd like a clear yes or no."*

- **YES (two views).** Significant — dual-view becomes something we can plan to enable. **Write down:** filename pattern / output channel for the side view; on by default or behind a flag; standard or hardware option. **Next:** ask for a sample file or documentation page by email; flag for review against Q-J2 in §6.7.8 (whether existing decoders silently drop it).
- **YES, BUT (only some models / firmware / revs).** **Write down:** which units in our fleet have it, by serial. Your prepared serial is one data point; ask about each FS6000 on site. **Next:** email asking for a per-serial breakdown.
- **NO (single-view only).** Closes the question cleanly; dual-view section stays deferred. **Write down:** which FS6000 models do or do not have it, even if ours don't, so an upgrade path is on file. **Next:** none.
- **"Let me get back to you".** Same as Q-C1 — get a name and email, send the written follow-up the same day, calendar reminder for 5 working days.

**Always write down:** if yes, the **filename pattern** for the side-view file. Dev needs that exact string.

Before hanging up, read your notes back so the vendor can correct misunderstandings, then thank them and end the call.

---

## Conversation 2 — ICUMS Ghana technical contact

One question. Keep it short — engineering at ICUMS will appreciate a focused call.

### Q-N1 — Can the API list seizures by date range, or only one at a time?

Roadmap link: [§6.11 Inbound post-hoc outcome adapter](../IMAGE-ANALYSIS-MODERNIZATION.md#611-inbound-post-hoc-outcome-adapter) — question Q-N1 in §6.11.15.

**Ask exactly:**

> *"For your seizure and outcome data — when a customs officer has finished a case and recorded the result — does your API let us ask 'show me everything decided between these two dates', or do we have to look up each declaration number one at a time? We're trying to figure out the best way to keep our records in sync with yours."*

If they ask why, the honest one-line answer is: *"We want to pull outcomes automatically once a day so our analysts can learn from final decisions. Per-declaration lookup would still work but is a lot of calls."*

- **YES (date-range supported).** Best outcome — once-a-day pull instead of hammering the per-declaration endpoint. **Write down:** endpoint name/path, the date field's name (decided-at / last-modified-at / received-at), maximum window the API allows (some APIs cap at 24 h), rate limit. **Next:** email asking for the API documentation (OpenAPI / swagger). The answer determines whether we need "hybrid mode" or just "pull mode" in §6.11.3.
- **YES, BUT (date range exists but restricted — small windows, recent dates only, separate endpoint per outcome type).** **Write down:** the exact restriction. A 1-hour window cap is fine; a 5-minute cap is a problem. Also ask whether webhooks (push) are an option — those would let us avoid polling. **Next:** email asking for written documentation, including rate limits.
- **NO (per-declaration only).** Expected; roadmap plans for it via hybrid mode. Not a blocker but more work. **Write down:** are webhooks available? Is there a "list declarations modified since timestamp X" endpoint — even if outcomes are per-declaration, knowing *which* to fetch is enough. **Next:** email confirming; flag to dev that the adapter must run in hybrid mode (push if available, otherwise per-declaration polling).
- **"Let me get back to you".** Reasonable — this is technical. **Write down:** name of the engineer who will reply and their email. **Next:** send the written follow-up the same day; calendar reminder for 5 working days.

**Always write down:** the contact's full name, role, and direct email. This is the liaison for the next few months of v2 work.

---

## Post-call wrap-up

Write up the answers in plain prose — not a form. One short paragraph per question. Copy the template below into a new file at **`docs/runbooks/vendor-call-2026-04-results.md`** (does not exist yet — create it after the calls).

```markdown
# Vendor call results — April 2026

## Call 1 — FS6000 supplier
**Date / time:** YYYY-MM-DD HH:MM
**Spoke with:** <name, role, email>
**Firmware version on our unit:** <version>

### Q-C1 (DICOS export)
<one short paragraph: yes / yes-with-conditions / no / pending, plus
costs or version numbers>

### Q-J1 (side-view output)
<one short paragraph: yes / no / pending, plus filename pattern if
applicable>

### Follow-ups owed by the vendor
- [ ] <item, e.g. spec sheet by email>

## Call 2 — ICUMS Ghana
**Date / time:** YYYY-MM-DD HH:MM
**Spoke with:** <name, role, email>

### Q-N1 (date-range vs per-declaration)
<one short paragraph: yes / yes-with-conditions / no / pending, plus
endpoint name, date-field name, window cap, rate limit>

### Follow-ups owed by the vendor
- [ ] <item, e.g. API documentation by email>
```

**What triggers the next iteration of work.** Once the results file exists and Q-C1, Q-J1, and Q-N1 each have a non-pending answer, the dev team can close those rows in §5.6, §6.7.8, and §6.11.15 and move the affected design slices off "deferred". If any answer is "let me get back to you" after 5 working days with no reply, escalate to whoever owns the vendor relationship before assuming silence equals no.

---

## Email templates — what to send afterward

Send these the same day, even if the vendor said they would reply. A short written restatement avoids "we never heard back" later.

### To the FS6000 supplier (after Call 1)

> Subject: Confirming today's call — FS6000 DICOS export and side-view output
>
> Hi <name>,
>
> Thanks for the call today. To confirm what we discussed:
>
> 1. **DICOS export** — you indicated <YES / YES-with-conditions / NO / you would check>. Could you send the spec sheet or written confirmation when convenient?
> 2. **Side-view output** — you indicated <YES / NO / you would check>. If yes, could you share a sample file or the documentation page showing the output filenames so we can confirm our system handles them?
>
> No rush — by end of next week is fine. We are gathering information for an internal design review; no commitment implied.
>
> Best,
> <your name>

### To the ICUMS Ghana technical contact (after Call 2)

> Subject: Confirming today's call — outcome API query options
>
> Hi <name>,
>
> Thanks for the call today. To confirm what we discussed:
>
> - The outcomes API <supports / does not support> query-by-date-range, with <conditions if any>.
> - Webhooks for outcome changes are <available / not available / something you would check>.
>
> Could you share the API documentation (OpenAPI or PDF) and any rate limits when convenient? End of next week is fine.
>
> Many thanks,
> <your name>
