# Master decisions needed

Append-only log of items the master coordinator escalated to the user. Each
entry preserves the question, the options the master saw, and a
recommendation. The user signs off (or chooses differently) by replying in
chat or, more durably, by editing the entry's "State" line.

---

## 2026-04-26 — G1 #3 — `ITenantContext.SetSystemContext` hard stop

**Triggered by:** Sprint 4 / G1 platform-tightening work item, sub-item #3
(NickFinance suite-wide system context). Branch
`plan/g1-platform-tightening`. Items #1, #2, #4, #5, #6 shipped on this
branch; #3 was held per the explicit hard-stop note in PLAN.md §15.2.

**Question.** What is the sanctioned mechanism for letting Finance run
cross-tenant system jobs (parent-tenant consolidated reporting, nightly
FX-rate publication, suite-wide chart-of-accounts updates) when today's
`ITenantContext.SetTenant(long)` rejects `tenantId <= 0` and every RLS
policy enforces `"TenantId" = COALESCE(current_setting('app.tenant_id'), '0')::bigint`?
The decision shapes the security model the next 6+ months of
multi-module work depends on, so the master is not making it solo.

This couples to G1 #4 (TenantId-nullable) which shipped its **schema**
(column nullable + partial index) but cannot exercise the AC test
through `nscim_app` in production without a system-context bypass —
RLS on `audit.events` rejects NULL-tenant inserts because
`NULL = anything` is NULL. Today's verification used `postgres`
(BYPASSRLS), which works for the AC but is not how production code
emits suite-wide events.

**Options the master sees.**

1. **Sentinel-based system context (PLAN.md §15.2 second bullet).**
   Add `SetSystemContext()` on `ITenantContext`; the
   `TenantConnectionInterceptor` writes `app.tenant_id = '-1'` (or any
   chosen sentinel that's NOT the existing fail-closed `'0'`). RLS
   policies that legitimately need cross-tenant reads or NULL-tenant
   writes add an `OR current_setting('app.tenant_id') = '-1'` clause.
   - **Pros.** Tiny code surface, no new column, RLS posture stays
     declarative. Each table opts in to system access by editing its
     policy.
   - **Cons.** Adds a magic sentinel. Easy to grep for in code
     review, but a tenant id of `-1` in a SQL trace is opaque.
     Mistyped policy clause (e.g. `'1'` instead of `'-1'`) silently
     opens cross-tenant reads — there's no compile-time check.

2. **Explicit `IsSystem` flag on `ITenantContext` (PLAN.md §15.2 third
   bullet, "Or simpler").** Add `bool IsSystem { get; }` with
   `SetSystemContext()` setting it to true. Interceptor branches on
   `IsSystem` and writes a sentinel value (still `-1` under the hood,
   but the API expresses intent). RLS policies use the same
   `OR current_setting('app.tenant_id') = '-1'` clause.
   - **Pros.** Readable call sites: `tenancy.SetSystemContext()` is
     unambiguous in code review. Same RLS surface as option 1.
   - **Cons.** Same sentinel risk as option 1; the layered
     `IsSystem` flag is just sugar.

3. **Per-context system role.** Create a separate Postgres role
   `nscim_system` with BYPASSRLS, used only by a small number of
   audited code paths (FX-rate worker, consolidated reporting, GL
   global updates). No ITenantContext change; the system worker
   opens its own DbContext with the system role's connection string.
   - **Pros.** No RLS policy changes — system role is invisible to
     normal code paths. Minimum blast radius. Aligns with the H3
     "per-context grants" precedent.
   - **Cons.** Two roles to manage, two connection strings to ship,
     two grants to keep in sync. Operational cost compounds with
     every new schema (Finance, HR, etc.). Doesn't address the
     `audit.events` NULL-tenant write path under `nscim_app`.

4. **No suite-wide events; force every cross-tenant fact to one row
   per tenant.** FX-rate published → write 1 row per tenant
   (denormalised). GL chart-of-accounts → same. ITenantContext stays
   as-is.
   - **Pros.** No security model change. Simplest mental model.
   - **Cons.** Storage blows up linearly with tenant count for
     genuinely-suite-wide facts. Subscribers watching for "FX rate
     published" hear N events for one publication — fan-out
     amplifies. Defeats the point of G1 #4 (TenantId-nullable).

**Recommendation.** **Option 2** — explicit `IsSystem` flag on
`ITenantContext` with a `SetSystemContext()` method, sentinel `-1` in
the connection setting, paired RLS policy clauses on the specific
tables that opt in. The flag makes the intent legible at every call
site (the security review's job is checking that
`SetSystemContext` calls match the audit register), and the sentinel
keeps the SQL surface declarative. Option 3's role split is cleaner
in isolation but compounds operational cost as more modules land —
G2's NickFinance is just the first; HR / Inventory will want the
same affordance. Pin a "system context audit register" doc in
`docs/` listing every code path that calls `SetSystemContext` and
the policy clauses that grant it visibility, refreshed at every
sprint.

**State.** Sub-task held. Worktree `C:/Shared/erp-v2-g1` preserved at
commit `<TBD on push>` of branch `plan/g1-platform-tightening`. Items
#1, #2, #4, #5, #6 already shipped on the same branch (build green,
26 new platform tests pass). The branch can merge to main without
#3; the missing system-context API is documented as a follow-up. G2
(NickFinance) cannot start until this decision lands.

**Resolved 2026-04-28** by user — picked **option 2**. Implementation
ships through the rolling master as **Sprint 5**'s single item:

- `bool IsSystem { get; }` on `ITenantContext`
- `void SetSystemContext()` method
- `TenantConnectionInterceptor` writes `app.tenant_id = '-1'` when `IsSystem`
- RLS policy clauses on the specific tables that need cross-tenant
  system access — start with `audit.events` (the G1 #4 NULL-tenant
  write path); add others ONE TABLE AT A TIME, never blanket
- New doc `docs/system-context-audit-register.md` listing every
  `SetSystemContext` caller + the policy clauses that grant them
  visibility, refreshed at each sprint boundary

**G1-3 unblocks G2's system-context dependency** but does NOT unblock
G2's domain-shape gating. G2 stays held until the user delivers the
~6 product calls (money type, voucher lifecycle, custodian/approver
model, ledger event shape, period locks, currency conversion).

**Implementation shipped 2026-04-29** — Sprint 5, commit `<sha-after-merge>`.
See `docs/system-context-audit-register.md` for the audit register that
tracks every `SetSystemContext()` caller going forward.

---

## 2026-04-28 — Rolling master cannot dispatch sub-agents in this environment

**Triggered by:** Rolling-master coordinator launch with full
drainable backlog (G1-3, FU-2..7, P1, P3) per the user's prompt.

**Question.** Sprint 4's master coordinator's per-sprint protocol relies
on a `Agent (general-purpose, run_in_background=true)` tool to spawn
worktree-isolated sub-agents — one per work item — and waits for their
push notifications before merging. The rolling-master prompt explicitly
references this pattern ("Spawn an Agent (general-purpose,
run_in_background=true) using the sub-agent prompt template").

This Claude Code session does **not** expose an Agent / Task /
sub-agent dispatch tool. The available tool surface is: Bash,
PowerShell, Read/Write/Edit, Glob/Grep, ToolSearch, TodoWrite, Monitor,
scheduled-tasks (cron-like), browser-control MCPs, and a few others —
no general-purpose sub-agent spawner.

Without that tool the rolling master cannot:
1. Run multiple work items in parallel (Sprint 6's FU-2..FU-7
   parallelism is the explicit reason that sprint was bundled).
2. Preserve the worktree-per-agent isolation that lets the master
   verify each merge as a separate review surface.
3. Stay within the context-budget halt rule — running all four sprints
   inline in this single conversation would shred the ~70% guard before
   Sprint 6 finished.

**Options the master sees.**

1. **Halt and surface.** Plan Sprints 5–8 in main (so the next master
   has nothing left to design), update the tracker, surface this
   decision, return. **(Chosen — see State below.)**
   - **Pros.** Preserves the master role's planning/execution
     separation. The next master (whether human-driven, a different
     CLI build with the Agent tool, or this same prompt re-run in an
     environment that exposes it) picks up with the plan already
     written.
   - **Cons.** Zero shipping this run. The drainable backlog is
     unchanged.

2. **Run sprints inline in this session.** Skip the worktree-per-agent
   model; do all the work item changes directly from the master
   conversation, branch-and-PR per sprint instead of per item.
   - **Pros.** Could ship Sprint 5 (single item, ~0.5 day) with budget
     to spare. Maybe Sprint 6's smaller items.
   - **Cons.** Loses parallelism — Sprint 6's six items would serialize.
     Loses the per-item review surface that catches drift. Halts
     mid-sprint with high probability once context-budget bites
     (especially in Sprint 7's runbook prose or Sprint 8's
     projection-plus-UI). Partial sprints leave the world in an awkward
     state, which the master prompt explicitly flags as a hard rule
     ("Never start a sprint you can't finish").

3. **Schedule each sprint as a scheduled-task** (`mcp__scheduled-tasks`).
   - **Pros.** Each task gets a fresh session with full context budget.
   - **Cons.** Scheduled tasks fire on a cron / fireAt schedule and
     create new sessions; they're not the same as in-session sub-agent
     dispatch. They also don't run in parallel out of the box, and
     coordination between them (waiting for completion, merging) would
     need an explicit handoff channel that this prompt doesn't define.
     Not a clean fit for "rolling master ships continuously" either.

**Recommendation.** Option 1. The master role's value is the
planning + dispatch + merge + verify discipline; without sub-agent
dispatch the role degrades to "single-threaded developer in master
clothing," which is worse than punting cleanly. The work is **fully
planned** at commit `6bf3236` — Sprints 5–8 are written into PLAN.md
with the exact same shape Sprint 4 used (Goal / Phases / Work items
with cards / Status snapshot / End-of-sprint smoke verification). A
follow-on master run in an Agent-tool-equipped environment can dispatch
straight from the plan without re-doing any design work.

**State.** Halted. Sprints 5–8 are fully written in PLAN.md (commit
`6bf3236`) and ready for execution. The drainable list is unchanged
from the start of this run: G1-3, FU-2..FU-7, P1, P3 (in that order).
G2 (NickFinance), P2 (edge node), and the image-analysis track remain
out of scope per the rolling-master prompt.

The user has three paths forward:

- **(a)** Re-run the rolling-master prompt in a Claude Code build /
  configuration that exposes a general-purpose sub-agent dispatch tool
  (the Task / Agent tool that Sprint 4's master used). Plans are
  already in main; the next master jumps straight to dispatch.
- **(b)** Author the work item branches manually (the user, or a
  different orchestrator) using PLAN.md §16–19 as the per-item brief.
- **(c)** Re-scope the master role for an Agent-less environment —
  e.g., serial sprints, single-item bundles, fewer halt-on-budget
  guards. This is a master-prompt redesign and itself a decision
  worth surfacing.
