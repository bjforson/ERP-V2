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
