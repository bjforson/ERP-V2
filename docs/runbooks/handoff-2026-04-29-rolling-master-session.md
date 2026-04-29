# Rolling-master session — handoff brief (2026-04-29)

> **For:** the next v2 dev session resuming the rolling-master orchestrator role.
> **From:** the v2 dev session that ran the master coordinator pattern across Sprints 5–11.
> **Status:** this session is **stopping** because the context window is too long. Eleven sprints shipped end-to-end today; nothing is in-flight; main is clean and pushed.
>
> **Per the coordination protocol** captured in `handoff-2026-04-29-image-analysis-session.md` §7, this is a **v2 dev session** handoff: source-code commits, migrations applied, sprint cadence. A parallel image-analysis session also handed off today — that session is `docs/runbooks/handoff-2026-04-29-image-analysis-session.md`. Read both before starting work.

---

## 1. What this session did

Drained two backlogs end-to-end via the master coordinator pattern (worktree-per-agent + Agent-tool dispatch + merge dance):

| Sprint | Theme | Items | Tests after | Main commit |
|---|---|---|---|---|
| 5 | System context mechanism | G1-3 | 51 | `5a1938c` |
| 6 | Followup sweep (6 parallel agents) | FU-2..FU-7 | 51 | `70a244e` |
| 7 | Operations runbooks | P1 | 51 | `f0e5afc` |
| 8 | Audit projection + notifications inbox | P3 | 60 | `f9a595d` |
| 9 | Followup-2 sweep (4 sequential agents) | FU-deploy / FU-userid / FU-host-status / FU-icums-signing | 91 | `4c05b5d` |
| 10 | **G2 NickFinance Petty Cash pathfinder** (NEW MODULE) | G2 | 185 | `3421b7b` |
| 11 | **P2 Edge node SQLite buffer + replay** | P2 | 208 | `c3274c5` |

Tests grew 51 → 208 (+157). Build clean throughout. Main HEAD currently at `556583a` (the post-Sprint-11 tracker close-out). All branches deleted (local + remote). All worktrees removed.

Mid-run product-call drain (after Sprint 8) captured 9 binding decisions covering G2 (NickFinance domain shape) + P2 (edge conflict resolution) + the 4-followup priority queue + image-analysis-track scope + deploy target. Spec lives in `docs/product-calls-2026-04-29.md`.

---

## 2. Repo state at handoff

- **Main:** `556583a` on `origin/main`. Clean. All branches `plan/*` deleted local + remote.
- **Working tree:** has the user's parallel image-analysis work as **uncommitted modifications + untracked files**. The image-analysis session's handoff doc covers what's there. Per the user's directive, this is to be **incorporated into the v2 sprint flow, not reverted** (precedent: the b416fd1 revert from earlier today; this time the resolution is integration).
- **Stash:** `stash@{0}` preserved with snapshot of conflicting `InspectionDbContext.cs` + `Modelsnapshot.cs` modifications from the FU-icums-signing merge window. The image-analysis session's later modifications to those files are now in the working tree (post-stash); the stash is a historical capture, probably superseded — verify before relying on it.
- **Worktrees:** only `C:/Shared/ERP V2` remains. Spin new ones per sprint via `git worktree add "C:/Shared/erp-v2-<id>" -b plan/<id>-<kebab> main`.

---

## 3. Where the load-bearing context lives

Never re-derive these from scratch — they are durable artifacts in the repo:

| Artifact | Path | What it is |
|---|---|---|
| Sprint progress (real-time) | `docs/sprint-progress.json` | Tracker JSON consumed by `apps/portal/Components/Pages/Sprint.razor` at `/sprint`. `currentSprint`, `history`, `backlog`, `followups`, `outOfScope`. |
| Product calls captured 2026-04-29 | `docs/product-calls-2026-04-29.md` | **Binding spec** for G2 (shipped) + P2 (shipped) + the four FUs (shipped). Future calls append-only. |
| Master decisions log | `docs/master-decisions-needed.md` | Append-only escalation log; G1-3 entry shows the format. |
| Sprint specs by section | `PLAN.md` §16 (G1-3) / §17 (FU-2..7) / §18 (P1) / §19 (P3) / §20 (pointer to product-calls doc) | Long-form sprint briefs with phases / work-item cards / smoke verification. |
| Architecture | `docs/ARCHITECTURE.md` §13 (NickFinance) / §14 (Edge nodes) / earlier sections (platform) | Module overviews + cross-module patterns. |
| System-context audit register | `docs/system-context-audit-register.md` | Every `SetSystemContext()` caller + the paired RLS opt-in clauses. Currently 4 entries: G1-3 audit.events, FU-icums-signing IcumsKeyRotation (none for v0), G2 FxRatePublishService, P2 EdgeReplayEndpoint.HandleAsync. |
| Migrations runbook | `docs/MIGRATIONS.md` | dotnet ef Windows env-var quirk (FU-5) + NickFinance prereqs. |
| Operations runbooks | `docs/runbooks/01-deploy.md` … `06-edge-node-stalled.md` | Six canonical runbooks (Sprint 7 + Sprint 11). |
| User-level project conventions | `C:/Users/Administrator/Documents/GitHub/NICKSCAN-CENTRAL--IMAGE-PORTAL/CLAUDE.md` | v1/v2 separation, security-posture rules, tenancy gotchas, Y:\ drive, mkcert/LocalSystem trap. **Read this first.** |

---

## 4. Open work + immediate routing

**Drainable backlog: empty.** No commitments for the next sprint.

**The Sprint 12 candidate is now obvious:** the image-analysis session's work needs integration. Per `docs/runbooks/handoff-2026-04-29-image-analysis-session.md` §4, suggested integration order is:

1. Adopt the design doc (`docs/IMAGE-ANALYSIS-MODERNIZATION.md`) as planning input — no code change.
2. Land routine plumbing (slnx + csproj refs + DI + DbContext wiring) — mechanical.
3. Land the Inference plugin family (Abstractions + OnnxRuntime + Mock + OCR.ContainerNumber).
4. Land the §6.5 threshold-calibration slice — **opinionated**, requires ratification of the new `NickERP.Inspection.Application` project shape.
5. Land the new entity scaffolds (§6.9 / §6.10 / §6.11) — pure storage, idle until consumed.
6. Decide on Phase 7.0 contract bumps — additive.
7. Adopt the `tools/` Python scaffolds.

**Architectural decisions to ratify or refactor** (per image-analysis handoff §5):
- (a) `Add_PhaseR3_TablesInferenceModernization` schema shape
- (b) Single `Application` project vs. fan-out per concern
- (c) Razor admin page in `Inspection.Web` vs. `apps/portal`
- (d) `ConfigJson` merge pattern in `ScannerIngestionWorker`

**Open followups (18):** in `docs/sprint-progress.json#followups`. Notable medium-severity ones:
- `FU-icums-cluster-key-ring` — data-protection key ring is per-host; clustered prod breaks
- `G2-FU-role-resolver` — NickFinance role checks are claim-based stubs
- `P2-FU-multi-event-types` — edge replay v0 is audit-event-only
- `P2-FU-edge-auth` — shared `X-Edge-Token` secret; needs per-edge API keys + rotation

A reasonable Sprint 12 shape: image-analysis integration (a 4-step bundle) + 1–2 medium FUs in parallel.

**Live-deploy smoke is also missing.** Every migration shipped today (G1-3, P3, FU-userid, FU-icums-signing, G2 ×2, P2 ×2) was generated via `dotnet ef migrations add` and verified by `migrations script --idempotent` but **never applied to a live DB**. The image-analysis session **did apply** its R3 + V0 migrations to `nickerp_inspection`. So the inspection DB has more applied migrations than the platform/audit/nickfinance DBs. This is a real coordination wrinkle worth sorting before any prod cutover.

---

## 5. Patterns this session relied on (load-bearing for the next master)

### 5.1 Sub-agent dispatch
- One worktree per work item: `git worktree add "C:/Shared/erp-v2-<id>" -b plan/<id>-<kebab> main`
- Spawn an `Agent (general-purpose, run_in_background=true)` with a fully self-contained brief. Briefs include: worktree path, branch, hard rules (v1 read-only, no `git add .`, no `dotnet ef database update`, etc.), Sprint 5+ binding context, file-paths-in-scope, acceptance criteria, report shape.
- Sequential vs parallel: parallel-safe items (Sprint 6 / FU-2..FU-7) dispatched in one message with multiple Agent tool calls. Sequential (Sprint 9 / four FUs) dispatched one at a time with merge cycles between.

### 5.2 Merge dance (the safe sequence)
1. `git stash push -m "<sprint> merge window" -- <list-of-tracked-modified-files>` — stash only specific paths; never the user's untracked parallel work.
2. `git fetch origin <branch>` then `git merge --no-ff <commit> -m "<message>"`.
3. Verify in detached worktree: `git worktree add --detach "C:/Shared/erp-v2-verify" HEAD` then `dotnet build` + `dotnet test`. Expect ≥<floor> passes.
4. `git push origin main`.
5. `git worktree remove "C:/Shared/erp-v2-<id>"` + `git branch -D plan/<branch>` + `git push origin --delete plan/<branch>`.
6. `git stash pop` — handle conflicts by `git checkout --ours` on the conflicted paths (preserve main's version) and let the user resolve from `stash@{0}` later.

**Hazard:** if the stash had unmodified files that overlap with the merge, `git commit` after the pop will silently include them. The b416fd1 revert was the recovery from this exact mistake — fix-forward via `git checkout HEAD~1 -- <files>` rather than force-push.

### 5.3 Tracker JSON updates
- One commit per status flip (dispatch / merge / sprint close-out).
- The Sprint razor page polls every 15s, so updates show live.
- `currentSprint.items` carries `status: "queued" | "in-progress" | "done"`, branch, commit (feature SHA), `mergeCommit` (merge SHA), agentId (informal), notes.
- After a sprint closes, move the whole `currentSprint` block into `history` (newest first), set `currentSprint: null` until the next dispatch.

### 5.4 Hard rules that bit during this run
- **v1 read-only.** No commits in `C:\Shared\NSCIM_PRODUCTION` or the launch-shell repo. v1 patterns may be referenced (e.g., v1's `Deploy.ps1`) but never modified.
- **`dotnet ef database update` doesn't work on Windows under nscim_app.** `dotnet ef migrations script --idempotent --output /tmp/x.sql` then `psql -U nscim_app -d <db> -f /tmp/x.sql`. Doc'd in `docs/MIGRATIONS.md`.
- **Em-dashes break Windows PowerShell 5.1.** ASCII `-` only in `*.ps1` files.
- **Plugin singleton + scoped DbContext.** Plugins are `AddSingleton`; capturing a scoped `DbContext` requires injecting `IServiceScopeFactory` and opening a fresh scope per call. FU-icums-signing hit this.
- **`SetSystemContext()` requires register-and-opt-in.** Every new caller must (a) land in `docs/system-context-audit-register.md` and (b) add `OR current_setting('app.tenant_id') = '-1'` in BOTH USING and WITH CHECK on the policy of every table it writes/reads.
- **Stage explicit paths only.** Never `git add .`, `-A`, or `-u`. The user has parallel work that must not be picked up.

---

## 6. What the next session should NOT re-do

- **Re-litigate the 9 product calls.** They're binding (`docs/product-calls-2026-04-29.md`). New calls go through the AskUserQuestion → append-only doc pattern.
- **Revert the image-analysis work.** Per the user's directive in the §0 of `handoff-2026-04-29-image-analysis-session.md`, integrate it. The reversal procedure documented there is **for reference only**, labeled as such.
- **Force-push to main.** CLAUDE.md rule. Fix-forward via revert commits.
- **Skip the smoke verification step.** Every merge to main was preceded by a clean detached-worktree build + test pass. Don't drop this discipline; the sub-agents catch most issues but the verify worktree caught the FU-host-status test-count miscount.
- **Try to "close" the image-analysis track inside this orchestrator.** It's user-driven; spawns its own master run when the user says go.

---

## 7. Recommended opening for the next session

**If the user opens with "continue / next sprint":**

> "Read `docs/runbooks/handoff-2026-04-29-rolling-master-session.md` and `docs/runbooks/handoff-2026-04-29-image-analysis-session.md`. Then check `docs/sprint-progress.json` and propose a Sprint 12 shape — the obvious candidate is the image-analysis integration sequence in §4 of the image-analysis handoff."

**If the user opens with a new product call:**

> "Read `docs/product-calls-2026-04-29.md` for the format precedent, then run AskUserQuestion to drain the call queue, then append a new `docs/product-calls-<date>.md` with the answers. Don't dispatch agents until the calls are captured."

**If the user opens asking about something specific that landed today (e.g., 'how does NickFinance work'):**

> Read PLAN.md §<n> for the sprint that shipped it, plus `docs/ARCHITECTURE.md` §13/§14, plus `docs/sprint-progress.json#history` for the commit chain. Don't re-derive from code unless the docs disagree.

**If the user opens with image-analysis-flavoured work:**

> Per the image-analysis handoff §7, declare the role first: design session (writes to `docs/` only) or v2 dev session (sprint cadence). If they're asking to ship the §6.5 wiring etc., that's v2 dev territory and goes through normal sprint flow.

---

## 8. Acknowledgements

- The user pivoted mid-run from "draft a plan" → "spawn parallel agents" → "interactive product-call drain via AskUserQuestion" → "continue draining sequentially." Eleven sprints in one day is the upper end of what this pattern produces; if the next session inherits a cleaner scope, expect 3–5 sprints/day rather than 11.
- The parallel image-analysis session was a coordination failure surface. The handoff doc's §7 protocol is the durable fix; the next session should hold to it strictly.
- Stash@{0} is preserved as belt-and-suspenders for the user's pre-image-analysis-session parallel work; verify against the working tree's current state before deciding it's needed.

End of brief. Next session takes over from `556583a`.
