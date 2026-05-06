# Runbook 14 — Pilot site stand-up

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

(§§8-12 follow in the next phase of this document.)
