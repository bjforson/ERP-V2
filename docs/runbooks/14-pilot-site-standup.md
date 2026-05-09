# Runbook 14 — Pilot site stand-up

> **Sprint 60 — scripted rehearsal.** Three new tools convert hand-stepped operator sections of this runbook into idempotent PowerShell scripts. See `tools/cutover-dryrun/`, `tools/security-scan/`, `tools/perf/` and the **Scripted alternative** callouts below.

> **Scope.** End-to-end operator-facing playbook for standing up
> ERP V2 at a brand-new pilot site, from "we want to pilot at site
> X" through "first real case decisioned end-to-end and the five
> pilot-acceptance gates have been Pass for 14 consecutive days."
> This runbook is the **assembly** that wires the existing per-area
> runbooks (HA, pgbackrest, PG17, NickFinance, comms-gateway) into
> a single, sequenced operator workflow.
>
> **This is the runbook that the deployment engineer reads first.**
> It does not replace runbooks 09-13 — it sequences them and adds
> the operator-facing pieces that no other runbook covers (site
> selection, tenant provisioning, first-pass smoke, gate execution,
> Phase V execution, real-traffic cutover, success / failure).
>
> **Vendor-neutral.** No site or vendor names appear below — wherever
> a concrete fact would be vendor- or country-specific, the runbook
> says "the customs authority" / "the operator" / "the scanner
> manufacturer" instead. The decision matrix in plan §13 (which
> *is* country-specific) is referenced but not duplicated.
>
> **Sister docs:**
> - [`14-pilot-acceptance-checklist.md`](14-pilot-acceptance-checklist.md)
>   — the operator-facing checkbox checklist that maps every §8-§11
>   verification to a single ticked item; the document the operator
>   physically copies and ticks during stand-up.
> - [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) — the
>   primary + standby pair this runbook's §4 hardware-provisioning
>   step prepares for.
> - [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
>   — the backup posture this runbook's §4-§5 stand up.
> - [`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md)
>   — the version posture every node in §4 must satisfy.
> - [`12-nickfinance-runbook.md`](12-nickfinance-runbook.md) — the
>   NickFinance module ops surface; in scope for stand-up if the
>   pilot tenant opts in.
> - [`13-comms-gateway-settings.md`](13-comms-gateway-settings.md)
>   — the per-tenant comms / SMTP keys §6 walks the operator
>   through.
> - [`15-pilot-acceptance-test.md`](15-pilot-acceptance-test.md) —
>   the developer-side end-to-end test that runs the same five
>   gates against a synthetic tenant; useful sanity check before
>   stand-up.
> - [`../security/audit-checklist-2026.md`](../security/audit-checklist-2026.md)
>   — the Phase V security audit the operator runs in §10 against
>   the pilot site.
> - [`../perf/test-plan.md`](../perf/test-plan.md) — the Phase V
>   load test the operator runs in §10 against the pilot site.
> - `~/.claude/plans/tingly-launching-quasar.md` §13 — the
>   pilot-site decision matrix referenced in §3.

---

## 1. What pilot stand-up is

### 1.1 The goal

Pilot stand-up is the production-shape deployment that validates
the **five pilot-acceptance gates** under real traffic at a single
customs site. The system has been "ready" since pre-pilot saturated
in Sprint 52, but "ready" is meaningless without an operator who
can actually deploy it. This runbook is the assembly.

The pilot succeeds when:

- All five gates on `/admin/pilot-readiness` are **Pass** for **14
  consecutive days**.
- No P0 finding remains open from the Phase V security audit
  ([`../security/audit-checklist-2026.md`](../security/audit-checklist-2026.md))
  or the Phase V perf test
  ([`../perf/test-plan.md`](../perf/test-plan.md)).
- The customs operator on site has signed off in writing.

The five gates (vendor-neutral, defined in
`platform/NickERP.Platform.Tenancy/Pilot/PilotReadinessGate.cs`):

| Gate | What it proves |
|---|---|
| `gate.scanner.adapter` | At least one scanner has a registered adapter and a successful capability check (vendor-neutral; FS6000 / ASE / mock all hit the same gate). |
| `gate.edge.roundtrip` | Edge node completed a full round-trip — capture → buffer → replay → audit row at central. |
| `gate.analyst.decisioned_real_case` | At least one analyst decisioned a non-synthetic case end-to-end. |
| `gate.external_system.roundtrip` | The external-system adapter (whichever is in scope at this site) completed a successful submission round-trip. |
| `gate.multi_tenant.invariants` | The active multi-tenant probe — cross-tenant impersonation reads, the system-context register check, and the cross-tenant export gate — all hold under live traffic. |

The first four are **observed** gates: the system watches the audit
trail for the qualifying event and flips Pass when it sees one.
"Not yet observed" is **not a failure** — it just means the operator
hasn't driven it yet. The fifth gate is an **active** probe — the
service runs cross-tenant reads and verifies they're rejected.

### 1.2 The participants

Pilot stand-up needs three groups working in concert. Each owns
clearly-bounded responsibilities; mismatched expectations between
the three has been the most common failure mode in the v1
deployment retrospectives, so name them up front.

| Role | Owns | Typical individual |
|---|---|---|
| **Operator (deployment engineer)** | Hardware provisioning (§4), network setup (§5), tenant provisioning (§6), running the runbooks. The operator is the person reading **this** runbook. | Internal IT / DevOps. |
| **Customs authority IT** | Network connectivity from the customs site to the central host, firewall rules for edge → central, on-site scanner network access, customs-account credentials for any vendor-system integration. | The customs IT contact named in the cooperation MOU. |
| **v2 dev team** | Code-level escalations during stand-up; analyst training material; sign-off on the pre-pilot test floor. Not on-call after the 7-day soak ends. | This team. |

> **The cooperation MOU is non-negotiable.** Section 6.4 documents
> it as a `tenancy.tenant_settings` row so its existence is
> first-class in the system. A pilot without a written MOU between
> the customs authority and the operator's organisation does not
> proceed past §3 (site selection).

### 1.3 The timeline

Typical pilot stand-up is **4-6 weeks** from §4 (hardware
provisioning) to §11.4 (sign-off). Padding for customs-side
delays usually drives the upper end. The phasing:

| Week | Phase | Sections |
|---|---|---|
| 1 | Pre-flight + hardware | §2 + §3 + §4 |
| 2 | Network + tenant provisioning | §5 + §6 |
| 3 | Scanner onboarding + smoke | §7 + §8 |
| 4 | Gate execution + Phase V | §9 + §10 |
| 5 | Cutover + 7-day soak | §11.1 + §11.2 |
| 6 | Sign-off | §11.3 + §11.4 |

If a phase slips, push the next phase by the same delta — do **not**
compress later phases to recover. The 7-day soak in §11.2 is a
hard floor; cutting it short is the kind of thing that catches a
P0 only after the customs operator is depending on the system.

### 1.4 What this runbook does NOT do

- **Choose the pilot site for you.** §3 walks the decision matrix;
  the actual site call is the operator's, with input from the
  customs authority. The runbook gives the framework, not the
  answer.
- **Replace the per-area runbooks.** §4 / §5 / §10 forward to
  runbooks 09-13 and `audit-checklist-2026.md` /
  `perf/test-plan.md` for the deep mechanics. Read those when
  forwarded; this runbook is the index, not the reference.
- **Cover post-pilot rollout.** Once the pilot has the 14-day
  green window, expanding to additional sites is out of scope here
  — that becomes a per-site replay of §4-§11 with the §3 site
  selection re-run for each new site.
- **Cover hot-swap of an already-running pilot.** If the pilot is
  in §11 soak and a critical fix lands, that's [`01-deploy.md`](01-deploy.md)
  (deploying a new build), not §11 (real-traffic cutover).

---

## 2. Pre-flight checks

Before §3 (site selection), verify the central infrastructure is
ready to *accept* a pilot. If any of these is open, fix it first —
running a pilot stand-up against a half-built central is a category
error.

### 2.1 PG17 cluster + standby

The central Postgres cluster is **primary + streaming standby on
PG17**, per [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md).

Verify:

```bash
# Both nodes report PG17.
psql -U postgres -h $PGPRI_HOST  -d postgres -c "SELECT version();"
psql -U postgres -h $PGSTBY_HOST -d postgres -c "SELECT version();"

# Replication is streaming.
psql -U postgres -h $PGPRI_HOST -d postgres -c "
  SELECT application_name, state, sync_state,
         pg_size_pretty(pg_wal_lsn_diff(sent_lsn, replay_lsn)) AS lag
  FROM pg_stat_replication;"
```

Expected: both nodes on `PostgreSQL 17.x`, one row in
`pg_stat_replication` with `state=streaming` and tiny lag. If the
standby is missing, finish [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md)
§5 first. If a node is on PG14 / PG15 / PG16, finish
[`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md)
first.

### 2.2 pgbackrest stanza + recurring cadence

A pilot without backups blocks at SEC-DB-4 / §10.2 below. Verify:

```bash
# Stanza exists; recent backup; recent WAL archive.
pgbackrest --stanza=nickerp info

# Expected: full backup ≤ 7 days old; incremental ≤ 24 h old;
# WAL archive timestamp ≤ 1 h old. If any is stale,
# 10-pgbackrest-backup-restore.md §6 is the fix.
```

The Windows posture choice (SSH-Linux backup host /
WSL2 / native v1) is per
[`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§5A. The recommended posture for a fresh pilot stand-up is the
**SSH-Linux backup host** — see §4.3 below for why.

### 2.3 Edge node hardware sourced

The pilot site's edge nodes need physical boxes ordered, racked,
and on the operator's network *before* §4. Lead time for procurement
is the most common cause of pilot timeline slip; if hardware is
not on hand at §2 time, restart §1.3's timeline planning with the
real procurement window.

Per-edge minimum spec (vendor-neutral):

| Resource | Floor | Rationale |
|---|---|---|
| CPU | 4 cores | Edge SQLite + plugin process + per-flush HTTP. |
| RAM | 8 GB | Buffer headroom for offline windows ≤ 24h. |
| Disk | 256 GB SSD | SQLite buffer + image staging; 256 GB carries ~ 2 weeks of low-volume site offline. |
| Network | Gigabit Ethernet to scanner; reliable WAN to central. | The §5.3 connectivity floor. |
| OS | Windows Server 2022 LTSC or Ubuntu 22.04 LTS | Match what runbook 06 documents. |

Ask the v2 dev team to sign off on per-site hardware sizing if
volume estimates exceed plan §13's "low-medium" baseline.

### 2.4 Customs operator engaged in writing

The cooperation MOU between the customs authority and the
operator's organisation is the **only** formal signal that the
pilot has cooperative intent on both sides. v1 retrospectives
attribute every "stand-up that became a multi-month negotiation"
to a missing or vague MOU — the v2 baseline is "MOU first, runbook
14 second."

The MOU minimum content:

- A named customs-side counterpart who is reachable for the entire
  pilot window.
- Signoff on the §3 site selection.
- Acknowledgement that the §10 Phase V tests will run on customs
  network and may briefly load it.
- Clear statement of who pays for what when scanner /
  network / hardware fails (operator vs customs).
- A graceful-failure clause — what happens if the pilot sign-off
  in §11.4 does not pass.

The MOU lives **outside** the v2 system as a paper document, but
its existence is mirrored as a `tenancy.tenant_settings` row at
§6.4. The mirror is searchable; the paper is the legal artifact.

### 2.5 Network connectivity baseline measured

Plan §13's hard gate ("connectivity reliability ≥ 95% uptime over
14 days") is a **prerequisite measurement**, not an aspiration.
Before §3 site selection:

- Run a 14-day connectivity probe from the prospective edge box
  to central. Sample every 60s; record uptime ratio.
- Record latency p50 / p95 / p99 over the same window.
- Record packet loss percentage.

Operator's tool of choice (e.g. a `cron`-driven `curl /healthz`
loop with a tiny SQLite log) is fine — the v2 system itself is
not yet at the site. Capture the report; attach to the §3 decision
matrix.

> **Why measure for 14 days?** A two-day measurement misses the
> weekly pattern (Monday traffic, Friday-night maintenance windows).
> 14 days catches one full operational cycle plus headroom. v1
> stand-ups that skipped this step accounted for three of the
> seven "edge-node-stalled" P1s logged in
> [`06-edge-node-stalled.md`](06-edge-node-stalled.md)
> hindsight aftermaths.

If any of the five §2 checks is open, **stop**. The pilot is not
ready to start.

---

## 3. Site selection scoring

Pilot site selection is the operator's call, with input from the
customs authority. The decision framework lives in plan-file §13;
this section is the **mechanical** walkthrough of how to apply it.

### 3.1 Hard gates

A candidate site must pass **all four** to be in the running:

- ☐ Site has at least one functional scanner of a class with a
  vendor-neutral plugin in `modules/inspection/src/NickERP.Inspection.Scanners.*`
  (today: FS6000 + ASE; the mock plugin is dev-only and does not
  qualify).
- ☐ Site has stable enough connectivity for online-first operation
  — ≥ 95% uptime over the §2.5 measurement window.
- ☐ At least one customs operator at the site is willing to
  participate in the pilot (named individual, captured in the
  §2.4 MOU).
- ☐ The operator's organisation has basic customs cooperation **in
  writing** for the site (the §2.4 MOU is signed for this site
  specifically).

A site that fails any hard gate is **not** rescued by a high
weighted score — site selection is gate-then-rank, not rank-only.

### 3.2 Weighted scoring

Among gate-passers, score each site 1-5 against each criterion;
multiply by weight; sum; highest weighted total wins. The criteria
and weights are locked in plan §13 — do not invent new ones during
stand-up.

| Criterion | Pilot prefers | Weight |
|---|---|---|
| Traffic volume | Lower (smaller blast radius) | 3 |
| Connectivity reliability | Higher | 3 |
| Local IT support presence | Higher | 2 |
| Operator cooperation | Higher | 2 |
| Scanner availability + condition | Higher | 3 |
| Geographical accessibility for v2 team | Higher | 1 |
| Operational simplicity (avoid edge cases) | Higher | 2 |
| Low political / contractual risk | Higher | 2 |

Worked example (numbers are illustrative):

| Criterion (weight) | Site A | Site B | Site C |
|---|---|---|---|
| Traffic (×3) | 3 (med) → 9 | 4 (low-med) → 12 | 1 (high) → 3 |
| Connectivity (×3) | 4 → 12 | 5 → 15 | 5 → 15 |
| IT support (×2) | 3 → 6 | 4 → 8 | 5 → 10 |
| Operator coop (×2) | 4 → 8 | 5 → 10 | ? (MOU pending) |
| Scanner avail. (×3) | 4 → 12 | 4 → 12 | 5 → 15 |
| Geo accessibility (×1) | 3 → 3 | 5 → 5 | 5 → 5 |
| Op simplicity (×2) | 4 → 8 | 4 → 8 | 2 → 4 |
| Low political risk (×2) | 4 → 8 | 4 → 8 | 3 → 6 |
| **Total** | **66** | **78** | **n/a (failed gate 4)** |

Site B wins. Site C is **disqualified** at gate 4 (MOU pending) —
its weighted score is irrelevant until the gate is closed.

### 3.3 Why "lowest-traffic gate-passer wins"

Plan §13 is explicit: **lower traffic = smaller blast radius**.
The first pilot is for catching the surprises that pre-pilot tests
missed; a high-traffic site amplifies any surprise into a customer-
visible incident. Once the pilot has the 14-day green window
(§11.4), expansion to higher-traffic sites is much lower-risk
because the surprises have been caught.

### 3.4 The system's runtime probes are the actual qualifier

Whatever site wins §3.1 + §3.2, the **real** qualifier is the
five gates passing for 14 days at §11.4. Site selection is the
informed guess that minimises the chance of the gates *not*
passing; the gates are the truth.

If §11.4 sign-off fails after 14 days, restart at §3 with a
different site — do **not** patch the failing site forever.

### 3.5 Output of §3

A single document — typically a one-pager — with:

- Each candidate site's hard-gate result (pass / fail).
- The weighted score table for gate-passers.
- The chosen site, named.
- A signature from the customs-side §2.4 counterpart confirming
  selection.

Attach to the §10 Phase V execution log so audit can trace why
this site was chosen. Without the §3 output document, §10's
auditor will cite SEC-AUDIT-7-equivalent ("system-context call
without documented reason") at audit time.

### 3.6 Tag the pre-pilot release

Before any hardware leaves §4 or any binary leaves a build host,
pin the source commit. This is the immutable reference the
auditor + the operator + the rollback playbook all point at.

Standard sequence (run on a workstation with `gh` authenticated,
or perform the second step via the GitHub web UI):

```bash
# 1. Lock the commit. Tag name pattern: pilot-<site>-<YYYY-MM-DD>
git fetch origin
git checkout main
git pull
git tag -a pilot-<site>-<YYYY-MM-DD> -m "Pre-pilot release for <site> launch"
git push origin pilot-<site>-<YYYY-MM-DD>

# 2. Cut a GitHub release from the tag. Title = tag name. Body should
#    include: pre-pilot saturation marker (current shippedSprints from
#    docs/sprint-progress.json), the site name, the §3.5 output doc
#    URL, and a one-line summary of what's in scope.
gh release create pilot-<site>-<YYYY-MM-DD> \
  --title "Pre-pilot release — <site>" \
  --notes-from-tag --verify-tag
```

If `gh` isn't available, create the release at
`https://github.com/<owner>/<repo>/releases/new?tag=pilot-<site>-<YYYY-MM-DD>`
and paste the body manually. Either path is acceptable; the
**tag** is the load-bearing artifact, the release is metadata.

**Why this matters.** `Deploy.ps1` (the publish step in §4–§5
combined) does `dotnet publish` from the working tree of whichever
commit is checked out on the prod box. Without a tag, "what we
shipped" drifts as commits land on `main` post-cutover. With a
tag, anyone running `git checkout pilot-<site>-<date>` reproduces
the exact deployed bits — needed for the §10.4 Phase V exit-gate
sign-off, §11.5 rollback, and any post-pilot forensic.

The tag pre-dates `Deploy.ps1` invocation. The operator should
`git checkout pilot-<site>-<date>` on the prod box before running
`Deploy.ps1` for the first cutover, so the publish output matches
the tagged commit.

> **Scripted alternative.** No tooling for this step yet —
> `tools/release-tag/` could ship in a future sprint to automate
> the tag-then-release pair (verify clean working tree, derive site
> from `--Site`, derive date from `Get-Date -Format yyyy-MM-dd`,
> validate `docs/sprint-progress.json` is in `currentSprint: null`
> state). Out of scope for Sprint 60.

**Output of §3.6.** The tag exists on `origin`, the GitHub release
points at it, and the operator's deploy plan names it. Reference
the tag URL in the §3.5 output document.

---

## 4. Hardware provisioning

The §3 site is locked. Now the operator buys / leases / racks
hardware. Two clusters of provisioning matter:

- **Central cluster** — the primary + standby + backup host that
  the pilot tenant points at. Already exists per §2.1 / §2.2 if
  the central is multi-tenant; if the pilot is the *first* tenant
  on a fresh central, this is where you stand the central up.
- **Edge cluster** — per-site boxes co-located with the scanners.

### 4.1 Postgres primary + standby

Spec per [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) §3
(prerequisites). Recap below; defer to runbook 09 for the install
mechanics.

| Resource | Primary | Standby | Notes |
|---|---|---|---|
| CPU | 8 cores | 8 cores | Match. Asymmetric capacity makes failover into the standby bumpy. |
| RAM | 32 GB | 32 GB | 25-30% allocated to Postgres `shared_buffers`. |
| Disk | 1 TB SSD (data) + 256 GB (WAL) | Same | WAL on a separate volume so a full data disk doesn't stop archiving. |
| OS | Same family on both nodes | Same | Mixed Linux + Windows produces collation drift; runbook 09 §3 calls this out. |
| PG version | 17.x | 17.x | Locked by runbook 11. |

The two boxes must be on the same LAN (sub-1 ms) for the streaming
replication to be HA-meaningful. Cross-region pairs are deferred
per ROADMAP §1 answer 3 — v0 is single-region.

After racking + OS install, run [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md)
§5.1-§5.8 to bring the cluster up.

> **Scripted alternative.** Run `tools/cutover-dryrun/run.ps1` for an automated, idempotent provision-clean-PG17 + apply-all-migrations rehearsal of these steps:
>
> ```pwsh
> pwsh tools/cutover-dryrun/run.ps1 -TargetUri <postgres-uri>
> ```
>
> Output: `tools/cutover-dryrun/reports/dryrun-{date}-migration-report.md`. See `tools/cutover-dryrun/README.md` for full options.

### 4.2 Edge node hardware

Per the §2.3 spec, one box per scanner site — typically 1-2 per
pilot site. Edge boxes can be physical or VMs; the constraint is
**predictable IO** for the SQLite buffer (a noisy-neighbour VM on
a busy hypervisor is the third most common cause of edge stalls
per `06-edge-node-stalled.md` retro). If using VMs, reserve CPU
+ disk IOPS at the hypervisor.

Each edge box runs:

- The edge agent (a small .NET worker that wraps `EdgeReplay`
  semantics).
- The SQLite buffer file, mode 0600, owned by the edge service
  account (per SEC-EDGE-2).
- The scanner adapter plugin (FS6000 / ASE), loaded in-process.

Edge nodes do **not** run a Postgres instance — they're stateless
relay points whose only durable state is the SQLite buffer. This
is intentional: a failed edge box gets replaced by re-imaging,
not by restoring a database.

### 4.3 Linux backup VM (recommended posture)

[`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§5A documents three Windows-host backup postures. For a fresh
pilot stand-up, the recommended choice is **SSH-Linux backup
host**:

| Posture | Pros | Cons |
|---|---|---|
| **A1 SSH-Linux (recommended)** | Full pgbackrest feature set; same shape as a Linux-only deployment; future-proof for cross-region DR. | One additional VM to manage. |
| A2 WSL2 | No extra VM; runs on the prod host. | WSL2 networking edge cases; lower throughput on large backups. |
| A3 native v1 | Simplest; single host. | pgbackrest's Windows native build is feature-limited; no `--type=incr` from before 2.50. |

A small Linux VM (2 vCPU / 4 GB / 100 GB) is enough for a pilot
site. Co-locate it with the central — same LAN as primary +
standby, distinct physical host from primary. Outbound network
to the off-site repository (S3 / Azure) per
[`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§5.6.

### 4.4 Sizing review checkpoint

Before placing hardware orders, the operator submits the §4.1 +
§4.2 sizing to the v2 dev team for review. Default acceptance
criterion: pilot peak (§13 connectivity / volume forecast) sits
below 30% of the spec's design ceiling, leaving headroom for
post-pilot growth. Sizing review is a written exchange; capture
the dev team's sign-off in the §3.5 output document.

---

## 5. Network setup

Hardware is on the floor. Now wire the network so the central
cluster, the edge nodes, and the customs IT systems can all reach
each other. Three layers:

### 5.1 TLS termination

Every entry point is HTTPS-only per SEC-TLS-1. The pilot stand-up
uses a real CA-signed certificate from day one — not a self-signed
or mkcert leaf, both of which fail under LocalSystem in v1
retrospectives. Cert provisioning:

- One cert per public hostname (pilot tenant's portal, the API).
  Wildcard certs are acceptable but not required.
- TLS 1.2 + 1.3 only. TLS 1.0 / 1.1 disabled at Kestrel per
  SEC-TLS-3.
- HSTS enabled with `max-age=31536000; includeSubDomains` per
  SEC-TLS-2.
- Cert renewal automated; calendar an audit reminder for 60 days
  before expiry.

Where a service-to-service call needs cert pinning (per the v1
`NICKSCAN_API_CERT_THUMBPRINT` lesson), use the **leaf** cert
thumbprint, never the CA root. Document the thumbprint in the
operator's runbook environment.

### 5.2 Cloudflare Access integration

Authentication is **CF Access**, not a v2-minted JWT, per
[`02-secret-rotation.md`](02-secret-rotation.md). Operator setup:

- Create a CF Access application for the pilot tenant's portal +
  API hostnames.
- Configure the application's audience tag — the value goes into
  `NickErp:Identity:CfAccess:ApplicationAudience` env var on every
  app host (per `01-deploy.md`).
- Configure the identity provider (operator's IdP — typically
  Google Workspace / Okta / Azure AD).
- Configure access policies — typical pilot policies:
  - "Customs operator users from the pilot site" → portal +
    case-detail pages.
  - "v2 dev team" → admin pages + diagnostics.
  - "Platform admin" → tenant management + pilot-readiness
    dashboard.

The CF Access JWKS URI is fetched at runtime per SEC-SECRETS-6 —
no signing keys to embed.

### 5.3 Edge → central network path

The edge box reaches central via either VPN or SSH tunnel. Two
options:

- **Site-to-site VPN** between the customs site and the operator's
  network. Recommended when the customs IT supports it and the
  operator already runs VPN infrastructure.
- **Per-edge SSH tunnel** to a relay host on the operator's network
  that proxies to central. Recommended when the customs IT does
  not allow site-to-site VPN.

Either path, the edge → central traffic is **encrypted in transit
twice** — once by TLS at the application layer (per SEC-TLS-7),
once by the VPN / SSH tunnel at the network layer. Both are
required: the application TLS does not depend on the network-layer
encryption being trustworthy.

Latency budget: edge → central round-trip p99 ≤ 500 ms, sustained.
A site that can't hit this floor will look "fine" until the buffer
backs up and `06-edge-node-stalled.md` fires.

### 5.4 Backup host SSH key distribution

If the §4.3 SSH-Linux backup posture is in use, the primary needs
SSH access to the backup VM (one-direction; backup VM does not
SSH into primary). Per
[`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§5A.5:

- Generate an ed25519 keypair on the primary as the postgres
  service account.
- Append the public key to `~postgres/.ssh/authorized_keys` on the
  backup VM, restricted by `from="<primary-IP>"`.
- Set the SSH config to use the keypair for `pgbackrest-backup`
  hostname; pgbackrest reads this.
- Test with `sudo -u postgres ssh pgbackrest-backup pgbackrest --version`.

The keypair has no passphrase (pgbackrest cannot prompt). This is
**only acceptable** because the keypair has no useful privilege
elsewhere — it can only run pgbackrest commands on the backup VM
under a restricted account. Audit register entry SEC-SECRETS-5 if
the backup VM also stores cipher-passes for cloud repos.

### 5.5 Network exit criteria

Before §6, verify the network is wired:

```bash
# From any app host: CF Access discovery resolves.
curl -fsSL "https://<pilot>.cloudflareaccess.com/cdn-cgi/access/certs" | head

# From the edge box: central is reachable over the encrypted
# tunnel.
curl -fsSL "https://<central-host>:5410/healthz" | head

# From primary: SSH to backup host works.
sudo -u postgres ssh pgbackrest-backup hostname

# From primary: standby is reachable on 5432.
nc -vz <standby-host> 5432
```

All four must succeed. If any fails, fix before §6 — running
tenant provisioning against an unreachable cluster is the same
category error as §2.

---

## 6. Tenant provisioning

The cluster is up + reachable. Now create the pilot tenant and
its first user. The flow is the **Sprint 21 first-user invite**,
documented end-to-end below.

### 6.1 Create the tenant

A platform admin (a v2 dev team member with `PlatformAdmin` scope)
creates the tenant via the `/tenants` portal page. Required fields:

- **Display name** — typically the customs site's friendly name.
- **Subdomain / external slug** — used in CF Access policies +
  per-tenant URLs. Lowercase + hyphens only; matches the
  `tenant_subdomain_regex` validator.
- **Initial state** — `Active` for a pilot. The other lifecycle
  states (`Suspended`, `SoftDeleted`, `PendingHardPurge`) are for
  post-creation flows; see `TenantLifecycleService` ops in
  [`01-deploy.md`](01-deploy.md).

The portal `/tenants/{id}` page lets the platform admin see the
new tenant's row and confirm `tenancy.tenants` has it.

### 6.2 Tenant lifecycle states

For reference — the four states a tenant can be in, defined in
`platform/NickERP.Platform.Tenancy/Entities/Tenant.cs`:

| State | Resolver behaviour | Reversible? |
|---|---|---|
| `Active` | Lets requests through. | n/a (default). |
| `Suspended` | Returns 403; data intact. | Yes — `ResumeTenantAsync`. |
| `SoftDeleted` | Returns 404; data retained for `HardPurgeAfter` days. | Yes until the `HardPurgeAfter` date — `RestoreTenantAsync`. |
| `PendingHardPurge` | Returns 404; awaiting operator confirmation. | Yes via `RestoreTenantAsync` until the operator runs `HardPurgeTenantAsync`; then irreversible. |

A pilot tenant lives entirely in `Active` state for §11. If
sign-off fails at §11.4, the lifecycle path is `Active → Suspended
→ SoftDeleted → PendingHardPurge → (operator-confirmed)
HardPurge`, with each step audited.

### 6.3 First-user invite

Per Sprint 21's invite flow:

1. The platform admin enters the customs operator's email on the
   `/tenants/{id}` page's "first-user invite" form.
2. `IInviteService.IssueAsync` creates an `InviteToken` row,
   computes its HMAC hash, stores the hash (the raw token never
   touches the database), and emails the raw token to the operator
   via `IEmailSender`.
3. The customs operator clicks the link in the email; the URL
   carries the raw token.
4. The portal's `AcceptInvite.razor` page redeems the token via
   `InviteService.RedeemAsync`. The token is single-use — the row
   flips to `Active=false` after redemption.
5. The customs operator is now a registered `IdentityUser` for
   the tenant with role `Tenant.Admin`.

Verification:

```bash
# An invite was issued.
psql -U nscim_app -d nickerp_platform -c "
  SELECT t.\"DisplayName\", i.\"Email\", i.\"IssuedAt\", i.\"RedeemedAt\"
  FROM identity.invite_tokens i
  JOIN tenancy.tenants t ON t.\"Id\" = i.\"TenantId\"
  WHERE t.\"Id\" = <pilot-tenant-id>
  ORDER BY i.\"IssuedAt\" DESC LIMIT 5;"

# After the customs operator accepts: the row has a RedeemedAt.
```

If the invite email does not arrive at the customs operator's
inbox, debug starting from `comms.email.smtp_host` per
[`13-comms-gateway-settings.md`](13-comms-gateway-settings.md) —
the per-tenant SMTP overrides in `tenancy.tenant_settings` may be
needed if the customs IT requires emails from a specific origin
domain.

### 6.4 Recording the cooperation MOU

Per §2.4, the cooperation MOU is mirrored as a tenant setting so
the system can reason about its existence. On the
`/admin/tenant-settings` page, set:

| Key | Value | Notes |
|---|---|---|
| `pilot.cooperation_mou.signed_at` | ISO 8601 date | The day the customs authority signed. |
| `pilot.cooperation_mou.counterpart_name` | string | The named customs-side contact. |
| `pilot.cooperation_mou.counterpart_email` | string | Reachable email; not necessarily an authoritative inbox. |
| `pilot.cooperation_mou.location_uri` | string | Path / URL to the paper document (e.g. SharePoint / signed PDF). |
| `pilot.cooperation_mou.expires_at` | ISO 8601 date | When the MOU expires; gate for renewal. |

These keys live alongside the existing `comms.email.*` keys in the
`tenancy.tenant_settings` table per Sprint 35's infrastructure
([`13-comms-gateway-settings.md`](13-comms-gateway-settings.md))
and are settable through the same `/admin/tenant-settings` portal
page. Audit row `nickerp.tenancy.setting_changed` is emitted for
every set; that row is the searchable trail of MOU history.

> **Why mirror the MOU as a setting?** The system already audits
> setting changes; mirroring the MOU's existence means the audit
> trail captures "MOU was in place at the time gate X
> transitioned" without an out-of-band paper trail. The §11.4
> sign-off can quote the audit row.

### 6.5 Verifying tenant provisioning

Before §7, confirm:

- The tenant exists in `tenancy.tenants` with `State=Active`.
- One `Tenant.Admin` user exists for the tenant via the §6.3
  invite.
- The §6.4 MOU settings are populated.
- The customs operator can sign in and reach `/launcher` (the
  Sprint 49 home page).

If the customs operator hits a 403 at `/launcher`, debug from CF
Access first (§5.2 access policies) before suspecting the tenant
state.

---

## 7. Scanner + adapter onboarding

The customs operator can sign in. Now register the scanners they
operate, give each scanner a registered adapter, issue an edge
node API key, and walk the **Sprint 46 onboarding wizard** to
capture the vendor metadata.

### 7.1 Scanner identity

In v2, a "scanner" is two rows:

- A **`ScannerDeviceInstance`** — the physical box, identified by
  a stable type code (`fs6000`, `ase`, ...) + per-tenant display
  name + location.
- A set of **`ScannerOnboardingResponse`** rows — the Annex B
  questionnaire answers per device-type, captured during onboarding.

The instance is created at `/scanners` (the Inspection.Web page).
The onboarding rows are populated through the wizard on the same
page.

### 7.2 Adapter selection

Vendor-neutral plugin selection — the page surfaces every
registered scanner plugin. Today the in-tree plugins are:

| Plugin | TypeCode | Status | Notes |
|---|---|---|---|
| `NickERP.Inspection.Scanners.Fs6000` | `fs6000` | Production-ready | Vendor manifest from FS6000 family scanners. |
| `NickERP.Inspection.Scanners.Ase` | `ase` | Stub (Sprint 50) | Contract-conformant; the vendor protocol stub fills in on-site. |
| `NickERP.Inspection.Scanners.Mock` | `mock` | Dev-only | Does not satisfy gate 1; pilot must not use. |

The operator picks the plugin that matches the physical scanner
manufacturer + model. If the site has a mix (e.g. one FS6000 +
one ASE), each is its own `ScannerDeviceInstance` with its own
plugin selection.

If the site has a scanner whose manufacturer has no plugin in
tree, **stop**. Pilot must be on a scanner with a working adapter;
authoring a new plugin is a multi-sprint workstream. Either pick
a different site (back to §3) or descope this scanner from the
pilot.

### 7.3 The Annex B 12-question wizard

Sprint 46's wizard captures vendor-survey metadata for the device-
type. It runs at `/scanners` under the "Onboard new scanner"
section. The 12 questions (the Annex B field codes per
`ScannerOnboardingService.Fields`):

| # | Field code | What it captures |
|---|---|---|
| 1 | `manufacturer_model` | Brand + model number. |
| 2 | `image_export_format` | The image format the scanner produces (FS6000 LUT JPEGs, ASE TIFF, etc.). |
| 3 | `api_sdk_availability` | Whether the vendor exposes an SDK / API the adapter can call. |
| 4 | `network_access` | LAN / serial / USB / etc. |
| 5 | `output_protocol` | Push / poll / file-drop / shared-folder. |
| 6 | `image_resolution` | Native resolution; matters for VRAM at OCR time. |
| 7 | `multi_view_support` | One image per scan or multiple views. |
| 8 | `metadata_fields` | What metadata the scanner attaches (container number / weight / etc.). |
| 9 | `auth_mechanism` | None / shared secret / cert / vendor-proprietary. |
| 10 | `firmware_version_visibility` | Whether the firmware version is interrogable. |
| 11 | `failure_mode_signalling` | How the scanner reports its own failure (flag bit / log / silent). |
| 12 | `vendor_support_contact` | Who the operator escalates to for scanner failure. |

The wizard is **operator-driven, not gating** — the system does
not block scanner registration on completed answers. The point is
to capture the vendor's posture at pilot-time so future adapter
authoring has somewhere to look. Audit row
`nickerp.inspection.scanner_onboarded` fires when the operator
saves.

Each answer persists per-field; the operator can leave fields
blank and fill them later, and re-answers append a new row (the
service reads "latest by RecordedAt"). The history is intentional —
compliance audits can walk the change log without a parallel
history table.

### 7.4 Per-scanner edge node API key

Each scanner needs its edge node to authenticate to central via
HMAC. Sprint 13 / T2's `EdgeAuthHandler` validates the per-edge
HMAC API key **before** tenant resolution — this is the SEC-AUTH-7
posture.

Issuance flow:

1. The platform admin calls `EdgeNodeApiKeyService.IssueAsync`
   for the new scanner's edge box (today: through a small
   admin endpoint or `dotnet run` against the audit DB; an
   admin UI is FU-edge-key-admin-ui-post-pilot).
2. The service generates a random key, computes its HMAC hash,
   stores the hash + a `KeyPrefix` (the first 8 chars of the
   raw key for human-recognisable issuance trace), and returns
   the **raw key one time** to the caller.
3. The operator transports the raw key to the edge box via a
   secure channel (operator's secret-rotation playbook, typically
   Bitwarden / 1Password).
4. The edge box stores the raw key in its config under
   `EdgeNode:HmacKey` env var.

Verification:

```bash
# The key is registered in audit.edge_node_authorizations.
psql -U nscim_app -d nickerp_platform -c "
  SELECT \"KeyPrefix\", \"IssuedAt\", \"RevokedAt\", \"TenantId\"
  FROM audit.edge_node_authorizations
  WHERE \"TenantId\" = <pilot-tenant-id>
  ORDER BY \"IssuedAt\" DESC;"

# The edge box can replay successfully.
curl -X POST "https://<central>:5410/api/edge/replay" \
  -H "Authorization: HMAC <key-hash>" \
  -H "Content-Type: application/json" \
  -d '{"events":[]}'
# Expected: 200 with empty result, or 401 if the key is wrong.
```

Each scanner site has its **own** edge key per SEC-EDGE-5. Shared
keys across scanners are forbidden — a compromised key for site
A must not let an attacker inject events for site B.

### 7.5 First scanner online

End-state for §7:

- One `ScannerDeviceInstance` row per physical scanner, with
  `IsActive=true` and a registered plugin.
- One Annex B questionnaire saved per scanner type.
- One `EdgeNodeApiKey` row per edge box, scoped to the pilot
  tenant.
- Edge boxes report `/edge/healthz` with `status=Healthy` and
  `queueDepth=0` (no buffer to drain yet).

Re-verify by signing in as the customs operator and visiting
`/scanners`: the registered scanners should appear in the list
with the correct location + plugin.

If §7 ends with no scanner online but every paper artifact in
place, the failure is almost always one of: (a) scanner is
physically off / disconnected, (b) edge box network path to
central blocked at firewall, (c) edge HMAC key mismatch between
issuance and edge config. Walk those three before deeper
diagnosis.

---

## 8. First-pass smoke

Tenant + scanner + edge node are wired. Now drive a synthetic
case end-to-end so the operator (and the customs operator) can
**see** the system working before any real customs case touches
it. The smoke is also the first non-trivial input that the §9
gates will observe — they will mostly remain "Not yet observed"
until §11 exposes the system to real traffic, but smoke gives the
operator a positive signal that the wiring is correct.

### 8.1 The synthetic-scan trigger

For each registered scanner, trigger a synthetic scan. There are
two paths:

- **At-the-scanner trigger.** The customs operator places a known-
  empty container under the scanner; the scanner produces a real
  scan with no anomalies. Best fidelity; depends on physical
  scanner availability.
- **Mock-shape trigger.** The v2 dev team (or the operator)
  triggers the adapter through a small admin endpoint that calls
  `IScannerAdapter.Capture` with a synthetic payload. Captured
  cases carry `IsSynthetic = true` so the §9 gates correctly do
  **not** count them toward the "real case decisioned" gate.

Either trigger must produce, at minimum:

- A `ScanArtifact` row in `inspection.scan_artifacts`.
- A `nickerp.inspection.scan_recorded` audit row.

Verification:

```bash
psql -U nscim_app -d nickerp_inspection -c "
  SELECT \"ScannerInstanceId\", \"ArtifactKind\", \"CreatedAt\"
  FROM inspection.scan_artifacts
  WHERE \"TenantId\" = <pilot-tenant-id>
  ORDER BY \"CreatedAt\" DESC LIMIT 5;"
```

### 8.2 Edge round-trip verification

For the edge round-trip, confirm the audit trail captured both
ends — capture at the edge, replay at central:

```bash
# The edge buffer drained to central — the audit row carries
# replay_source = "edge" in its payload.
psql -U nscim_app -d nickerp_platform -c "
  SELECT \"OccurredAt\", \"EventType\", \"Payload\"->>'replay_source' AS source
  FROM audit.events
  WHERE \"TenantId\" = <pilot-tenant-id>
    AND \"EventType\" = 'inspection.scan.captured'
  ORDER BY \"OccurredAt\" DESC LIMIT 5;"
# Expected: at least one row with source = 'edge'.
```

If the row has `replay_source IS NULL` or the row is missing
entirely, the edge → central round-trip didn't fire. Walk
[`06-edge-node-stalled.md`](06-edge-node-stalled.md) before
proceeding; smoke fails this step until the round-trip works.

### 8.3 Analyst inbox visibility

The customs operator (logged in as the tenant admin from §6.3)
visits the analyst review queue (`/reviews/queue`) and confirms
the synthetic scan appears as a case in the inbox. The case
detail page (`/cases/{id}`) opens cleanly and shows the captured
artifact.

This step verifies the **end-user surface** works — analysts can
*find* the cases the system created. v1 retrospectives flag this
specifically because cases were sometimes correctly captured but
hidden from analysts due to AnalysisService routing
mis-configuration.

### 8.4 Audit trail completeness

For each smoke event, walk the `/audit-log` page (or the
`audit.events` table) and confirm each step in the chain produced
an audit row:

| Step | Audit event |
|---|---|
| Scanner captures | `nickerp.inspection.scan_recorded` |
| Edge replays | `inspection.scan.captured` (with `replay_source = 'edge'`) |
| Case created | `nickerp.inspection.case_created` |
| Analyst opens case | `nickerp.inspection.case_viewed` |
| Analyst saves a finding | `nickerp.inspection.finding_saved` |
| Analyst issues verdict | `nickerp.inspection.verdict_set` |

Six audit rows per case. Missing rows mean the audit pipeline has
a gap — investigate via the `SourceContext` filter in Seq for the
component whose row is missing.

### 8.5 Synthetic-case verdict

The customs operator decisions the synthetic case (e.g. "no
anomaly found"). The case row gets `IsSynthetic = true`; the
verdict-set audit row carries the same flag. Gate 3 (analyst
decisioned a non-synthetic case) does **not** flip on a synthetic
case — that's by design.

### 8.6 Exit criteria for §8

Before §9, every line below must be true:

- ☐ Each registered scanner produced at least one synthetic
  capture, end-to-end through edge → central → analyst → verdict.
- ☐ Each scanner's `ScanArtifact` row count > 0.
- ☐ Edge audit chain is complete (six rows per case).
- ☐ Customs operator confirms the case appears in their analyst
  inbox.
- ☐ Customs operator can decision the case.

If any line is open, **stop**. Smoke is a hard gate before §9 —
the readiness gates are designed for live traffic; running them
against an unsmoke-able system surfaces the wrong "what's needed"
guidance.

---

## 9. Pilot-readiness gate execution

§8 confirmed the wiring is correct on synthetic cases. §9 walks
the operator through the runtime gates, defined in
`platform/NickERP.Platform.Tenancy/Pilot/PilotReadinessGate.cs`
and surfaced at `/admin/pilot-readiness`. The dashboard auto-
refreshes every 30 s; the goal is to drive each gate from "Not
yet observed" → Pass.

### 9.1 The dashboard

Sign in as the platform admin and visit `/admin/pilot-readiness`.
The dashboard renders one card per gate, plus three sub-pills
under the multi-tenant invariant gate (`rls_read_isolation` +
`system_context_register` + `cross_tenant_export_gate`).

Each card surfaces:

- The gate's friendly name + technical ID.
- Its current state — `Pass` (green) / `Fail` (red) / `Not yet
  observed` (amber).
- A "proof event" link if Pass — clicking opens the audit log
  filtered to the qualifying event.
- A "what's needed" note if `Not yet observed` — the operator's
  prompt for what to drive next.

The dashboard never crashes if an internal probe dies; failures
surface as `Fail` with the reason in the note.

### 9.2 What each gate measures + how to drive it

#### `gate.scanner.adapter` — Scanner adapter wired

**What it measures:** at least one scanner has produced a
`nickerp.inspection.scan_recorded` audit event for the tenant.
The plugin loaded (the adapter ran the scan).

**Driver:** §7 + §8.1 already drove this. After the first
synthetic scan lands, the gate flips to Pass within 30 s.

**If still Not yet observed after smoke:** the audit event isn't
firing — check the scanner adapter's log for swallowed exceptions.
Most likely cause: the plugin loaded but the per-scan write to
`audit.events` is failing on a missing column / RLS violation.

#### `gate.edge.roundtrip` — Edge round-trip

**What it measures:** an `inspection.scan.captured` audit event
exists with `Payload->>'replay_source' = 'edge'`. The edge node
replayed at least one event.

**Driver:** §8.2 already drove this. After smoke, the gate flips
to Pass.

**If still Not yet observed after smoke:** edge → central
replay isn't running. Walk
[`06-edge-node-stalled.md`](06-edge-node-stalled.md).

#### `gate.analyst.decisioned_real_case` — Analyst decisioned a real case

**What it measures:** a `nickerp.inspection.verdict_set` audit
event for a case where `inspection.cases.IsSynthetic = false`.

**Driver:** **A real case must flow through the system.** This
gate is the one that defines "the pilot has actually started" —
synthetic smoke does NOT flip it. The driver is §11 (real-traffic
cutover): the customs operator decisions a real case from the
customs pipeline.

**If still Not yet observed during §11:** real cases aren't
reaching the analyst inbox. Possible causes: feature-flag rollout
hasn't been advanced; AnalysisService routing has zero analysts;
the real customs feed isn't producing scans (network / scanner
hardware fault).

#### `gate.external_system.roundtrip` — External system round-trip

**What it measures:** an `inspection.OutboundSubmission` row
exists with `Status = 'accepted'` and `LastAttemptAt IS NOT NULL`.
Some external system (the per-site authority's submission API)
accepted a submission.

**Driver:** depends on the external system in scope at the pilot
site. The submission worker dispatches outbound submissions per
[`05-icums-outbox-backlog.md`](05-icums-outbox-backlog.md). After
the first real case is decisioned (gate 3), the system attempts
submission to the external authority; on the first acceptance,
the gate flips.

**If still Not yet observed during §11:** the outbound dispatcher
is failing. Check `inspection.outbound_submissions` for rows in
`Status = 'failed'` and walk `OutboundSubmissionDispatchWorker`'s
log for the underlying exception.

#### `gate.multi_tenant.invariants` — Multi-tenant invariants

**What it measures:** the **active** probe. Three sub-checks,
each must Pass for the gate to Pass:

1. `rls_read_isolation` — the probe attempts a cross-tenant read
   under tenant A's context for tenant B's rows; RLS must reject.
   If this sub-check fails, **stop** — RLS has regressed and the
   pilot cannot proceed.
2. `system_context_register` — the probe scans every
   `SetSystemContext` caller in code against the
   `docs/system-context-audit-register.md` list. New unregistered
   callers fail this sub-check.
3. `cross_tenant_export_gate` — the probe attempts to download
   tenant A's export under tenant B's identity; the API must
   reject.

**Driver:** Pass on first refresh of a healthy system. The probe
runs every refresh + every 60 s background tick; if any sub-check
flips Fail, that's a P0 incident.

**If Fail:** the dashboard's note shows which sub-check failed.
Route the failure:

- `rls_read_isolation:fail` → tenant RLS regression, P0. Re-run
  the verification from
  [`02-secret-rotation.md`](02-secret-rotation.md) §5.6 ("restore
  minimal-privilege state").
- `system_context_register:fail` → new code lands a
  `SetSystemContext` caller that isn't registered. P0. The fix is
  in code (add the register entry), not in ops; escalate to the
  v2 dev team.
- `cross_tenant_export_gate:fail` → SEC-TENANT-9 regression. P0.

### 9.3 The §9 → §10 handoff

§9's gates 1, 2, 5 should Pass after smoke. Gates 3 + 4 stay
"Not yet observed" until §11 introduces real traffic — that's by
design. Do **not** wait for gates 3 + 4 before §10 — Phase V
runs against the smoke-validated system, not against live customs
traffic.

If any gate is Fail at the §9 → §10 handoff, fix it before §10.
Phase V on a system with a known gate failure is wasted Phase V
cost.

---

## 10. Phase V execution

The system is wired, smoke passes, and gates 1/2/5 are Pass. Now
run Phase V — the operator-facing security audit + the perf load
test — against the pilot-shaped system. Both must pass acceptance
gates per their checklists before §11 (real-traffic cutover) is
allowed.

### 10.1 Phase V security audit

The auditor (a v2 dev team member who has not implemented the
code under review) copies
[`../security/audit-checklist-2026.md`](../security/audit-checklist-2026.md)
to a per-pilot file `audit-{site}-{date}.md`, walks every SEC-*
item, and ticks each as Pass / Fail. Failures get an `AUD-{n}`
finding ID with severity (P0 BLOCK pilot / P1 fix-before-launch
/ P2 fix-by-launch+1mo / P3 backlog).

Per the audit checklist's "Phase V exit criteria" section, the
audit is **complete** when:

- All P0 items pass.
- All P1 items either pass OR have a documented fix-before-launch
  ticket.
- P2 + P3 items have backlog tickets.
- The `system-context-audit-register.md` is reviewed +
  countersigned by a second engineer.
- The pilot site's edge keys are issued + tested (already done in
  §7.4).
- Backup + restore drill executed once on the pilot's data shape
  (see §10.3 below).

When all five lines are checked, Phase V security is complete.
The auditor's per-pilot file is committed to the pilot
documentation repository (a `pilots/{site}/audit-{date}.md` path
or equivalent).

> **Scripted alternative.** Run `tools/security-scan/run-audit.ps1` for an automated, idempotent walk of the SEC-* checklist that emits the per-pilot file ready for the auditor's countersignature:
>
> ```pwsh
> pwsh tools/security-scan/run-audit.ps1 -Site <site>
> ```
>
> Output: `tools/security-scan/reports/audit-{site}-{date}.md`. See `tools/security-scan/README.md` for full options.

### 10.2 Phase V perf load test

The operator runs the load tests defined in
[`../perf/test-plan.md`](../perf/test-plan.md) against the pilot
infrastructure. The harness is `tests/NickERP.Perf.Tests/` (Sprint
30 + Sprint 55 scaffolding); the runbook lives in the perf plan
itself.

The tests run at three scales per the plan's "headroom multipliers"
section:

- **1x** — pilot peak — must pass acceptance gates.
- **5x** — Tema-shaped projection — should pass with degraded but
  acceptable latency.
- **10x** — stress / breaking-point discovery — informative; not
  a gate.

Acceptance gate per the plan's §3 "baseline targets": at 1x load,
every endpoint's p99 latency sits under its budgeted ceiling. At
5x load, p99 sits within the documented degraded-mode tolerance.

If the 1x test fails, **stop**. The pilot will not survive its
own peak. Resolve the bottleneck (typically: add CPU / RAM at the
primary, tune Npgsql pool, profile the slow endpoint) and re-run.

> **Scripted alternative.** Run `tools/perf/run-phase-v.ps1` for an automated, idempotent NickPerf wrapper that drives the 1x acceptance run and records p99 latencies vs the budgeted ceilings:
>
> ```pwsh
> pwsh tools/perf/run-phase-v.ps1 -TargetUri <url> -Site <site> -Profile 1x
> ```
>
> Output: `tools/perf/reports/perf-{site}-{date}.md`. See `tools/perf/README.md` for full options.

### 10.3 Backup + restore drill

Per SEC-DB-4 + SEC-DB-5, the pilot site's data shape needs a
backup + restore drill. The drill walks
[`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§7 (full restore) + §8 (PITR) on a fresh box, restoring a copy of
the pilot's seeded-but-pre-cutover data. Capture the drill log;
attach to the pilot documentation.

The drill is **mandatory** before §11. Operator's tendency
("we'll run the drill after cutover") has historically produced
"backups exist but no one tested restoring them" outcomes; that
is exactly the failure mode the SEC-DB-4 P0 prevents.

### 10.4 Phase V exit gate

Before §11, all three of these are true:

- ☐ Phase V security audit signed off (per §10.1 exit criteria).
- ☐ Phase V perf load test passes 1x at all p99 budgets (per
  §10.2).
- ☐ Backup + restore drill completed on pilot's data shape (per
  §10.3).

A pilot that proceeds to §11 with any of these open is failing
the gate. The customs operator's sign-off in §11.4 will not be
defensible without these three items checked.

---

## 11. Real-traffic cutover

Phase V is signed off. Now expose the system to real customs
traffic, gradually. The cutover is **not a flag flip** — it's a
multi-day ramp with the operator + analyst in the loop.

### 11.1 Feature-flag gradual ramp

v2's `FeatureFlag` infrastructure (Sprint 35) is the cutover
control. Per-tenant flag keys live in `tenancy.feature_flags`; the
admin UI is `/admin/feature-flags`. Recommended ramp keys for the
pilot tenant:

| Key | Day 1 | Day 3 | Day 7 |
|---|---|---|---|
| `pilot.real_traffic.scan_capture_enabled` | true | true | true |
| `pilot.real_traffic.percent_routed` | 10 | 50 | 100 |
| `pilot.real_traffic.bypass_synthetic_filter` | false | true | true |

Each flag flip is audited (`nickerp.tenancy.feature_flag_toggled`).
If a flag flip causes a regression, flipping it back is the
rollback — no redeploy needed.

The "10% / 50% / 100%" routing is implemented by the customs-side
intake — the v2 system does not itself sample. The cutover is
**operator-driven**: the customs IT routes a portion of the
real-time scan feed to v2 while the rest continues on v1 (or
manual workflow); v2 receives only the routed portion until the
pilot is signed off.

### 11.2 Operator + analyst training

Before day 1, the customs operators who will use the system in
production receive training. Training surfaces (the analyst-facing
pages they should know):

| Page | Surface | Built in |
|---|---|---|
| `/launcher` | Module tile launcher (Sprint 49) | Sprint 29 / Sprint 49 |
| `/cases/{id}` | Case detail with image gallery + findings + verdict | Sprint 31 / Sprint 34 |
| `/reviews/queue` | Analyst inbox (priority-ordered) | Sprint 34 |
| `/reviews/bl/{caseId}` | BL review form | Sprint 34 |
| `/reviews/ai/{caseId}` | AI triage page | Sprint 34 |
| `/reviews/audit/{caseId}` | Supervisor audit review | Sprint 34 |
| `/admin/rules` | Rule admin (per-tenant strict mode) | Sprint 28 / Sprint 48 |
| `/admin/reports` | Reports dashboard | Sprint 33 |
| `/notifications` | Notifications inbox | Sprint 35 |

The v2 dev team's training material is the read-only walkthrough
of each page — what each button does, what state changes happen,
how to recover from a misclick. The customs operator who's been
the §6.3 first user is the **trainer** for additional analysts;
this scales because day-1 training is a single screen-share, not
a multi-day course.

Training completion gate: each trained analyst signs in and
demonstrates one full case decisioning end-to-end against a
synthetic case (re-using the §8 mock trigger). The demonstration
is captured in the pilot documentation as evidence the analyst
is ready for live cases.

### 11.3 Seven-day soak window

After day 7's "100% routed" flag flip, the system has all real
customs traffic for the pilot site. The 7-day soak runs from
day 7 to day 14:

- Daily check-ins between operator + customs operator + v2 dev
  team. Standing 15-min slot; cancel if nothing to discuss.
- Daily snapshot of `/admin/pilot-readiness` — all 5 gates Pass
  every day. A single Fail in the 14-day window resets the soak
  to day 1.
- Seq dashboards reviewed daily for anomalies (failed-auth rate,
  outbound submission failure rate, edge buffer depth).
- Backup + restore drill **every weekend** during soak (operator
  cron — abbreviated drill, not the full quarterly drill).

If a P0 incident fires during soak, the soak resets. If a P1
fires, the soak continues but the P1 must be resolved within 48 h
or the soak resets. P2 + P3 do not reset the soak.

### 11.4 Sign-off criteria

After 14 days of all-Pass gates, the pilot is signed off. The
sign-off involves three signatures:

| Signatory | What they sign | Where |
|---|---|---|
| Customs operator (named §2.4 counterpart) | "The system is fit for use at this site." | Paper document; mirrored to `pilot.signoff.customs_signed_at` tenant setting. |
| Operator (deployment engineer) | "All §10 + §11 criteria met. No P0 / P1 open." | `pilot.signoff.operator_signed_at` tenant setting. |
| v2 dev team lead | "Code-side support for this pilot is live." | `pilot.signoff.dev_team_signed_at` tenant setting. |

All three settings written within a 24 h window means the pilot
is **live**, not pilot. Subsequent operations follow the existing
runbooks (01 deploy / 02 secret rotation / 06 edge-node-stalled /
etc.); §14's runbook is no longer the active document.

### 11.5 The "what could still go wrong"

Even a clean §11.4 sign-off can fall over post-pilot. Common §11.4
+ 30 days regressions:

- **Backup cadence drifts.** The weekend drill skipped one weekend
  and then two; alert thresholds in
  [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
  §10.1 catch this.
- **Operator team changes hands.** New on-call hasn't read this
  runbook. Review the per-area runbooks 01-13 with new team;
  their familiarity is the recovery posture, not yours.
- **Customs operator is reassigned.** The §2.4 MOU counterpart
  leaves the role. Renew the MOU with the new counterpart;
  update the tenant settings; re-confirm cooperation.

These are not §11.4 sign-off failures — they're post-pilot
operations. They live under [`01-deploy.md`](01-deploy.md) +
[`02-secret-rotation.md`](02-secret-rotation.md).

---

## 12. Pilot success / failure handling

### 12.1 What success looks like

The pilot is **successful** when:

- All five gates have been Pass for 14 consecutive days.
- No P0 or P1 finding is open from the Phase V audit.
- The customs operator on site has signed off in writing (§11.4
  sign-off complete).
- Analyst feedback during the 7-day soak is positive — analysts
  decision cases at expected throughput, no recurring complaint
  about a missing capability or a confusing surface.

A successful pilot transitions the tenant from "pilot" to "live":

- The `pilot.signoff.*` settings are set.
- The `tenancy.feature_flags` "pilot.real_traffic.*" rows can be
  removed (cutover is complete; no further routing decisions).
- The §11.3 daily check-ins drop to weekly, then to as-needed.
- The runbook in active reading rotation switches from §14 to
  §01-§13.

A successful pilot is the prerequisite for **expansion** —
extending v2 to additional customs sites. Each new site re-runs
§3-§11 against its own §3 site selection. Sites can run in
parallel once the central infrastructure has the capacity (§4
sizing review).

### 12.2 What failure looks like

The pilot is **failing** when:

- A gate has been Fail at any point in the 14-day window (the
  soak resets to day 1).
- A P0 finding from the Phase V audit cannot be closed.
- The customs operator declines to sign off.
- Analyst feedback during the soak surfaces a category of bug or
  missing capability that requires a multi-sprint v2 dev team
  effort to fix.

Failure is **not a binary** — a soak that resets twice and then
completes on the third attempt is still a successful pilot, just
on a longer calendar. Failure-as-end-state means the pilot cannot
proceed at this site, period.

### 12.3 Rollback procedure

If the pilot is declared a failure (§12.2), the rollback steps:

1. **Flip the cutover flags off.** The §11.1 feature flags go to
   `false` / `0%`. Real customs traffic stops reaching v2.
2. **Soft-delete the tenant.** Per §6.2, the tenant lifecycle
   moves to `SoftDeleted`. Data is retained for `HardPurgeAfter`
   days (default 90); the customs operator can no longer reach
   the system.
3. **Capture a final tenant export.** Per Sprint 25's tenant
   export tooling, an export job runs against the soft-deleted
   tenant and produces a single archive. Archive is delivered to
   the customs authority per the §2.4 MOU's "graceful failure"
   clause.
4. **Decommission the edge nodes.** Edge boxes at the pilot site
   are wiped (per §4.2 they're stateless); edge HMAC keys are
   revoked via `EdgeNodeApiKeyService.RevokeAsync`.
5. **Postmortem.** Write a postmortem covering: which gate(s)
   failed, what the root cause was, what the v2 dev team needs
   to do before the next pilot attempt, what the operator needs
   to do differently. The postmortem is the input to §3 site
   re-selection.
6. **Hard-purge after retention window.** After
   `HardPurgeAfter` days, the operator runs
   `TenantPurgeOrchestrator.PurgeAsync` per §6.2; data is gone.

The rollback is **not a recovery** — once the tenant is
soft-deleted, the pilot at this site is over. Resuming the same
tenant is not the right call; if the same site is to be re-piloted
later, run §3-§11 again under a new tenant against a
new MOU.

### 12.4 The "neither success nor failure yet" state

A pilot that is past §11.1 day-1 but pre-§11.4 sign-off lives in
the active-pilot state. The active-pilot state is the longest
phase by calendar time (the 7-day soak) and is the most
operationally demanding (daily check-ins, daily gate review,
weekly drill). Treat it as a sustained P2 — not an emergency, but
not background either.

Active pilots have the operator's full attention. Other
deployment / migration / refactor work pauses for the duration of
§11 unless explicitly approved by the v2 dev team lead.

---

## 13. References

- [`14-pilot-acceptance-checklist.md`](14-pilot-acceptance-checklist.md)
  — operator-facing checkbox checklist mirroring §8-§11.
- [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) — primary
  + standby pair stand-up; referenced in §2.1, §4.1.
- [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
  — backup posture; referenced in §2.2, §4.3, §5.4, §10.3.
- [`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md)
  — PG17 version posture; referenced in §2.1, §4.1.
- [`12-nickfinance-runbook.md`](12-nickfinance-runbook.md) —
  NickFinance ops; in scope for the pilot tenant if NickFinance
  is opted in at §6.
- [`13-comms-gateway-settings.md`](13-comms-gateway-settings.md)
  — per-tenant comms settings; referenced in §6.3 (invite email
  delivery) + §6.4 (MOU mirror).
- [`15-pilot-acceptance-test.md`](15-pilot-acceptance-test.md) —
  the developer-side end-to-end test that runs the same five
  gates against a synthetic tenant.
- [`01-deploy.md`](01-deploy.md) — deploying a new build; the
  active runbook post-§11.4.
- [`02-secret-rotation.md`](02-secret-rotation.md) — secret
  rotation; the active runbook post-§11.4.
- [`05-icums-outbox-backlog.md`](05-icums-outbox-backlog.md) —
  outbound submission backlog; gate 4 driver.
- [`06-edge-node-stalled.md`](06-edge-node-stalled.md) — edge
  node stall recovery; §8.2 + gate 2 driver.
- [`../security/audit-checklist-2026.md`](../security/audit-checklist-2026.md)
  — Phase V security audit; §10.1.
- [`../perf/test-plan.md`](../perf/test-plan.md) — Phase V perf
  load test; §10.2.
- [`../system-context-audit-register.md`](../system-context-audit-register.md)
  — register that gate 5 sub-check 2 cross-references against code.
- `~/.claude/plans/tingly-launching-quasar.md` §13 — pilot site
  decision matrix; §3 references.
- `platform/NickERP.Platform.Tenancy/Pilot/PilotReadinessGate.cs`
  — gate IDs + semantics.
- `apps/portal/Components/Pages/PilotReadiness.razor` — the
  dashboard surface; §9.1.

