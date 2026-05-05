# Runbook 09 — Postgres HA setup (primary + streaming standby + manual failover)

> **Scope.** Standing up the v2 Postgres cluster as a **primary +
> streaming standby** pair on PostgreSQL 17, with **manual** (operator-
> driven) failover. This is the operational shape locked by ROADMAP §1
> answer 3 (2026-05-02): "primary + streaming standby with documented
> manual failover (Patroni deferred); pgbackrest backups (full +
> incremental + PITR); all reads from primary (standby is HA-only);
> single region (cross-region DR later); EF Core / Npgsql pooling only
> (no pgBouncer); locked to PostgreSQL 17."
>
> This runbook covers the **first-time** stand-up plus the manual
> failover / failback procedures. Backups + PITR are a separate
> concern — see [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md).
> The PG17 lock + upgrade-from-older path lives in
> [`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md).
>
> **Sister docs:**
> - [`01-deploy.md`](01-deploy.md) — application-host deploy mechanics.
>   The HA story below adds a "where do the host's connection strings
>   point" wrinkle that 01 doesn't cover; see §7 here.
> - [`02-secret-rotation.md`](02-secret-rotation.md) — `nscim_app`
>   posture; the replication role added by §4 here is a separate
>   account from `nscim_app` and rotates on its own schedule.
> - [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) —
>   mandatory backup-before-DDL pattern; same posture applies before
>   any failover that involves repointing connection strings.
> - `ROADMAP.md` §1 (locked answer 3) — the operational shape this
>   runbook delivers.

---

## 1. Symptom

You're standing up HA. There is no symptom — this runbook is a
**planned change** path. Use it when:

- You're deploying ERP V2 to a host where Postgres has been
  single-host through the dev cycle and the operational target is
  primary + standby. Today's `C:\Shared\ERP V2` dev posture is
  single-host; this runbook is the migration path to the locked
  shape.
- A second physical box (or VM) becomes available and you need to
  bring up the streaming standby against the existing primary
  without downtime.
- The original primary has failed and you need to **promote the
  standby** to primary so the application can resume — see §7 (manual
  failover).
- The original primary returns to service and you need to rebuild
  it as the new standby — see §8 (failback).

If a host is currently throwing
`FATAL: the database system is in recovery mode` it is connected to
a standby and trying to write — pick §7 (failover) if the primary
is gone, or fix the application's connection string if the primary
is fine and the app pointed at the wrong node.

## 2. Severity

| Trigger | Severity | Response window |
|---|---|---|
| Routine first-time stand-up (planned cutover from single-host) | n/a — operator-initiated | as scheduled |
| Adding a second standby (capacity / ops parallelism) | n/a — operator-initiated | as scheduled |
| Primary down, standby healthy → manual failover required | P1 | inside 30 min |
| Standby down, primary healthy → no app-visible incident; rebuild | P2 | inside 4 h (replication-lag alerts will fire) |
| Both nodes down simultaneously | P1 | inside 30 min, see [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md) §7 |
| Failback: original primary retakes the role after recovery | n/a — operator-initiated | as scheduled |

**v0 explicitly does not auto-failover.** Patroni / pg_auto_failover
remain deferred per ROADMAP §1 answer 3. Rationale: at v0 cardinality
(one pilot location, one operator on-call) auto-failover's split-brain
risk outweighs its 30-minute MTTR savings. Revisit when the operator
team grows past one person.

## 3. Quick triage (60 seconds)

Before you start, answer:

- **Is this a stand-up, a failover, or a failback?** They share the
  same primitives (`pg_basebackup`, `pg_ctl promote`,
  `primary_conninfo`) but the order matters and the rollback shapes
  differ. Pick the right §5 / §7 / §8.
- **Are both nodes on the same PG major version?** Streaming
  replication does not cross major versions. If primary is PG17 and
  the prospective standby is PG16, **stop** and finish
  [`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md)
  first.
- **Do both nodes share the same OS bit-width + libc + ICU?** Binary
  WAL replay is locale-sensitive. Mixed OS families
  (Windows primary + Linux standby, or vice-versa) will appear to
  work for a while and then desync on collation-dependent indexes.
  v0 posture is **same OS family on both nodes**.
- **Is `archive_command` already set on the primary?** If yes, this
  is not a fresh stand-up — you're inheriting state from
  [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md).
  That's fine; just don't blow away the existing config in §4.
- **Is anyone else applying changes?** Two operators promoting in
  parallel will produce arbitrary outcomes. One operator at a time.

## 4. Diagnostic commands

All commands assume bash (Git Bash on Windows, WSL, or Linux). The
v1 install path for psql on Windows is
`/c/Program Files/PostgreSQL/17/bin/psql.exe`; v1's running install
on the existing prod box may still be on `PostgreSQL/18/bin/`
(historical, the directory was named for the install bundle's
year-tag, not the major version) — check with `psql --version`
before assuming the path. Linux installs have `psql` on PATH.

### 4.1 Confirm both nodes are reachable + on PG17

```bash
# On primary candidate (alias: PGPRI):
PGPRI_HOST="<primary-ip-or-hostname>"
PGSTBY_HOST="<standby-ip-or-hostname>"

# Per-node connectivity + version.
psql -U postgres -h $PGPRI_HOST  -d postgres -c "SELECT version();"
psql -U postgres -h $PGSTBY_HOST -d postgres -c "SELECT version();"
# Expected: PostgreSQL 17.x on x86_64-... on both. If a row says
# PostgreSQL 16.x or 14.x, stop — see runbook 11.
```

### 4.2 Confirm role state on each node

```bash
# Is this node currently a standby?
psql -U postgres -h $PGPRI_HOST  -d postgres -c "SELECT pg_is_in_recovery();"
psql -U postgres -h $PGSTBY_HOST -d postgres -c "SELECT pg_is_in_recovery();"
# Expected on a fresh stand-up:
#   primary candidate: f (false  - not in recovery, this is a primary)
#   standby candidate: f (false) - it's a fresh primary too, hasn't been initialised yet
# After §5 finishes:
#   primary: f (false)
#   standby: t (true - in recovery, replaying)
```

If the primary candidate already returns `t`, it has been
incorrectly initialised as a standby. Stop and reconcile — likely
someone followed §5.5 against the wrong host.

### 4.3 Confirm reachable network paths for replication

The standby must be able to open a Postgres connection to the
primary on `5432/tcp` and read enough WAL fast enough to keep up.

```bash
# From the standby, can it reach the primary?
nc -vz $PGPRI_HOST 5432
# Expected: "Connection to <host> 5432 port [tcp/postgresql] succeeded!"
```

If the connect fails, fix firewall before continuing — `pg_basebackup`
in §5.5 will hang otherwise.

### 4.4 Snapshot existing config (if not a fresh stand-up)

If the primary candidate is already in production, snapshot its
config before you change anything:

```bash
# On the primary:
psql -U postgres -d postgres -c "SHOW wal_level;"
psql -U postgres -d postgres -c "SHOW max_wal_senders;"
psql -U postgres -d postgres -c "SHOW wal_keep_size;"
psql -U postgres -d postgres -c "SHOW archive_mode;"
psql -U postgres -d postgres -c "SHOW archive_command;"
```

A pre-existing `wal_level = replica` + `archive_mode = on` means
this is an inherited node from the pgbackrest stand-up
([`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)).
Keep those settings; only add `max_wal_senders` and the replication
slot in §4.

## 5. Resolution — first-time stand-up

The shape: fresh primary + fresh standby on two boxes; primary is
the existing live data store (or a freshly-loaded one); standby
catches up via `pg_basebackup` and then streams.

The order of operations is **non-negotiable**:

1. Configure primary (`postgresql.conf` + `pg_hba.conf` + replication
   role + replication slot) → restart primary.
2. Verify primary is accepting replication connections.
3. **Backup before any standby work** — full pgbackrest backup per
   [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
   §5. The standby init below is a recoverable mistake; the time
   saved by skipping the backup is not worth it.
4. `pg_basebackup` on standby (initialises its data dir from the
   primary's current state).
5. Configure standby (`primary_conninfo`, `standby.signal`).
6. Start standby.
7. Verify replication is streaming.

### 5.1 Configure primary — `postgresql.conf`

Settings to add or confirm (these are **minimum** values for v0;
tune `wal_keep_size` higher if the standby is on a flaky link):

```
# WAL + replication
wal_level = replica
max_wal_senders = 10
wal_keep_size = 2GB
max_replication_slots = 10
hot_standby = on        # only matters on the standby, but harmless here

# Archive (required by runbook 10 too; if 10 hasn't run yet, add
# them now and runbook 10 will inherit)
archive_mode = on
archive_command = 'pgbackrest --stanza=nickerp archive-push %p'
```

Edit `postgresql.conf` (typical paths:
`/etc/postgresql/17/main/postgresql.conf` on Debian-family Linux,
`/var/lib/pgsql/17/data/postgresql.conf` on RHEL-family,
`C:\Program Files\PostgreSQL\17\data\postgresql.conf` on Windows).

> **Why `replica` not `logical`?** v2 has no logical-replication
> consumer (no CDC pipeline today). `logical` increases WAL volume
> ~30% for no benefit at v0. Revisit if a logical consumer ever
> lands.

### 5.2 Configure primary — `pg_hba.conf`

Append a **replication** line that allows the standby's IP to
connect as the replication role:

```
# TYPE  DATABASE        USER          ADDRESS              METHOD
host    replication     nickerp_repl  <standby-ip>/32      scram-sha-256
```

`scram-sha-256` is the v0 minimum; do not allow `trust` even on a
private subnet — replication credentials live for the cluster's
lifetime and an unauthenticated path is the kind of thing the next
audit catches.

### 5.3 Create the replication role

```bash
psql -U postgres -h $PGPRI_HOST -d postgres <<'SQL'
-- Replication role; ONLY the REPLICATION privilege, no DB access.
CREATE ROLE nickerp_repl WITH REPLICATION LOGIN PASSWORD '<set-strong-password>';

-- Physical replication slot - prevents WAL deletion before the
-- standby has consumed it. Slot name is opinionated; use the
-- standby host's short name.
SELECT pg_create_physical_replication_slot('standby_<short-name>');
SQL
```

Capture the password in the operator's password manager before
proceeding to §5.5 — the standby's `primary_conninfo` will need
it. Naming convention for slot: lowercased, alphanumerics + `_`
only, max 63 chars.

> **Why a slot, not just `wal_keep_size`?** A replication slot
> tells the primary "do not delete WAL until this consumer has
> acknowledged it." Without a slot, a standby that falls behind
> by more than `wal_keep_size` worth of WAL needs to be re-seeded
> from a fresh `pg_basebackup`. With a slot, the primary holds WAL
> indefinitely — at the cost of disk-fill risk if the standby
> stays disconnected for too long. v0 chooses the slot for safer
> default; monitor disk in §9.

### 5.4 Restart primary + verify replication endpoint

```bash
# Linux (Debian/RHEL families):
sudo systemctl restart postgresql

# Windows:
Restart-Service postgresql-x64-17

# Connectivity smoke from the standby host:
PGPASSWORD='<repl-password>' psql -U nickerp_repl -h $PGPRI_HOST \
  -d postgres -c "SELECT pg_is_in_recovery();"
# Expected: f (the primary is not in recovery).
```

A `28P01: password authentication failed` means §5.2 / §5.3 / the
captured password disagree — fix before proceeding.

### 5.5 Backup before initialising the standby

**Mandatory.** Take a full pgbackrest backup per
[`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§5 *now*, before §5.6's `pg_basebackup` rewrites the standby's data
dir. Two reasons:

- The standby init wipes the standby box's `PGDATA` — if the box
  had any prior data, it's gone.
- A pgbackrest backup on the primary captures a known-good restore
  point in case §5.6 fails halfway and you need to roll back the
  primary's WAL position.

If the primary is on Windows, the §5.5 invocation depends on the
pgbackrest posture per
[`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§5A.5. Recommended (SSH-Linux backup host) runs the backup on the
Linux backup VM; WSL2 v0 runs it via `wsl.exe -u postgres pgbackrest
…` on the prod host; native v1 runs `pgbackrest.exe …`.

### 5.6 Initialise standby — `pg_basebackup`

On the **standby** box, with Postgres stopped:

```bash
# Linux:
sudo systemctl stop postgresql
sudo -u postgres rm -rf /var/lib/postgresql/17/main/*

sudo -u postgres pg_basebackup \
  -h $PGPRI_HOST \
  -U nickerp_repl \
  -D /var/lib/postgresql/17/main \
  -P -R \
  -X stream \
  -S standby_<short-name>
```

Flag-by-flag:
- `-P` — progress reporting (operator-friendly).
- `-R` — write `standby.signal` and `primary_conninfo` into the
  data dir automatically. This is the PG17-supported replacement
  for the deprecated `recovery.conf` file.
- `-X stream` — stream WAL during the backup so the standby is
  never behind the start point.
- `-S` — bind to the slot created in §5.3, so WAL accumulates in
  the slot during this backup.

`PGPASSWORD='<repl-password>'` either via env or `~/.pgpass` (mode
0600) so the basebackup doesn't prompt.

Windows path differs — `pg_basebackup -D "C:\Program Files\PostgreSQL\17\data"`
with the Windows service stopped via `Stop-Service postgresql-x64-17`.

### 5.7 Verify standby config

After `pg_basebackup` finishes, check the data dir contents:

```bash
sudo -u postgres ls -la /var/lib/postgresql/17/main/standby.signal
sudo -u postgres cat /var/lib/postgresql/17/main/postgresql.auto.conf
# Expected: a primary_conninfo line referencing $PGPRI_HOST,
# nickerp_repl, the slot name, and the password (or a passfile=
# reference if you used ~/.pgpass).
```

If `standby.signal` is missing, `-R` didn't run — re-do the
basebackup.

### 5.8 Start standby

```bash
# Linux:
sudo systemctl start postgresql

# Windows:
Start-Service postgresql-x64-17
```

Tail the standby's log for the line that confirms it's streaming:

```
LOG:  entering standby mode
LOG:  consistent recovery state reached at 0/3000148
LOG:  database system is ready to accept read-only connections
LOG:  started streaming WAL from primary at 0/4000000 on timeline 1
```

The phrase **"started streaming WAL"** is the canonical "we are
live" log line. If you see "could not connect to the primary
server" instead, fix the auth (§5.2) or the network (§4.3).

## 6. Verification

In this order:

### 6.1 Replication is streaming

```bash
# On the primary - which standbys are connected?
psql -U postgres -h $PGPRI_HOST -d postgres <<'SQL'
SELECT application_name, client_addr, state, sync_state,
       pg_size_pretty(pg_wal_lsn_diff(sent_lsn, replay_lsn)) AS lag_bytes,
       backend_start
FROM pg_stat_replication;
SQL
```

Expected: one row per standby, `state = 'streaming'`, `sync_state =
'async'` (v0 is async — see §10), small `lag_bytes`. If
`pg_stat_replication` is empty, the standby has not connected.

### 6.2 Standby thinks it's a standby

```bash
psql -U postgres -h $PGSTBY_HOST -d postgres -c "SELECT pg_is_in_recovery();"
# Expected: t
```

### 6.3 Replication lag

```bash
# On the standby:
psql -U postgres -h $PGSTBY_HOST -d postgres <<'SQL'
SELECT pg_last_wal_receive_lsn() AS received,
       pg_last_wal_replay_lsn()  AS replayed,
       pg_wal_lsn_diff(pg_last_wal_receive_lsn(),
                       pg_last_wal_replay_lsn()) AS apply_lag_bytes,
       now() - pg_last_xact_replay_timestamp() AS apply_lag_time;
SQL
```

`apply_lag_time` should be < 1 s on a healthy LAN-connected pair.
If it climbs past 60 s — the §10 alert threshold — a slow disk or
saturated network is the usual cause; check `iostat` / `iftop`.

### 6.4 Application reads / writes target the right node

```bash
# What does the application's connection actually hit? Check from
# inside the app's connection string.
ConnectionStrings__Inspection="Host=<host>;..." \
  ConnectionStrings__Platform="Host=<host>;..."

# Sanity test - this should succeed on the primary and fail on the
# standby:
psql -U nscim_app -h <connection-host> -d nickerp_inspection \
  -c "INSERT INTO inspection.cases DEFAULT VALUES RETURNING 1;" \
  2>&1 | head -1

# A successful insert means you're hitting the primary. The error
#   ERROR:  cannot execute INSERT in a read-only transaction
# means you're hitting the standby - re-route the application's
# connection string before claiming the cutover is done.
```

> **Reads from primary only — locked.** Per ROADMAP §1 answer 3,
> the standby is **HA-only**, not a read-replica. Don't add a
> read-routing layer; route every connection at the primary.

### 6.5 Restore minimal-privilege state

The standby init ran as `postgres` (superuser). After the standby
is up, confirm the application still connects as `nscim_app`:

```bash
# Per docs/runbooks/02-secret-rotation.md §5.6:
psql -U postgres -d postgres -c \
  "SELECT rolname, rolsuper, rolbypassrls
   FROM pg_roles WHERE rolname = 'nscim_app';"
# Expected: super=f, bypassrls=f.
```

`nickerp_repl` is **only** for replication — it should never appear
in an application connection string. Confirm:

```bash
psql -U postgres -d nickerp_inspection -c \
  "SELECT DISTINCT usename, application_name
   FROM pg_stat_activity
   WHERE datname = 'nickerp_inspection' AND state IS NOT NULL;"
# Expected: every row shows usename = nscim_app, never nickerp_repl.
```

## 7. Manual failover — standby becomes primary

Trigger: primary is unreachable / corrupt / decommissioning. App is
read-only on the standby and writes are failing. Goal: make the
standby the new primary; point the application at it; rebuild the
old primary as the new standby (§8) when it comes back.

**Allowed downtime window: ≤ 5 minutes for the application** —
mostly the connection-string roll plus host restart. Postgres
itself promotes in seconds.

### 7.1 Pre-flight — confirm primary is actually gone

Don't promote a standby while the primary is alive — that path
splits the brain. Confirm the primary is down:

```bash
nc -vz $PGPRI_HOST 5432
# Expected on a real outage: "Connection refused" or timeout.
```

If `nc` succeeds, the primary is up. If only the application can't
reach it (network partition between app + primary, but standby can
still reach primary), do **not** promote — the primary will keep
accepting writes from any other client that can reach it. Fix the
network instead.

### 7.2 Stop the application's connections to the primary

```bash
# Stop the app hosts so they aren't holding open
# connections that would silently fail mid-statement after promotion.
Stop-Service NickERP_Inspection_Web
Stop-Service NickERP_Portal
# (And any future module services per docs/runbooks/01-deploy.md.)
```

This is the §3 "is anyone else applying" check, applied to apps —
no host should hit the cluster while it's mid-failover.

### 7.3 Capture the standby's last replayed LSN

For the postmortem timeline:

```bash
psql -U postgres -h $PGSTBY_HOST -d postgres -c \
  "SELECT pg_last_wal_replay_lsn(), pg_last_xact_replay_timestamp();"
# Save these values - they're the "we lost <delta> of WAL" answer
# if the primary turns out to have committed beyond this point and
# can't be recovered.
```

### 7.4 Promote the standby

```bash
# Linux:
sudo -u postgres pg_ctl promote -D /var/lib/postgresql/17/main

# Windows:
& "C:\Program Files\PostgreSQL\17\bin\pg_ctl.exe" promote `
  -D "C:\Program Files\PostgreSQL\17\data"
```

Tail the (former) standby's log:

```
LOG:  received promote request
LOG:  redo done at 0/AB...
LOG:  selected new timeline ID: 2
LOG:  archive recovery complete
LOG:  database system is ready to accept connections
```

The phrase **"selected new timeline ID"** is the canonical "we are
now primary" log line. The `pg_is_in_recovery()` query now returns
`f` on this node.

### 7.5 Update the application's connection strings

Repoint every host's `ConnectionStrings__*` env-var stanza at the
new primary's host. For NSSM-supervised services:

```powershell
nssm set NickERP_Inspection_Web AppEnvironmentExtra `
  "+ConnectionStrings__Inspection=Host=<new-primary>;Port=5432;Database=nickerp_inspection;Username=nscim_app;Password=$NICKSCAN_DB_PASSWORD" `
  "+ConnectionStrings__Platform=Host=<new-primary>;Port=5432;Database=nickerp_platform;Username=nscim_app;Password=$NICKSCAN_DB_PASSWORD"
```

Same for `NickERP_Portal` and any future module services. The
`AppEnvironmentExtra +` syntax appends, so prior env vars (notably
`ASPNETCORE_*` and `NICKSCAN_DB_PASSWORD`) survive.

> **Why connection-string roll, not DNS / floating IP?** v0 is
> manually-failovered; the operator is in the loop, so direct
> repointing is the simplest reliable path. If a future floating-IP
> or DNS-CNAME pattern is wanted, it's a layer on top of this
> runbook, not a replacement.

### 7.6 Restart application hosts

```powershell
Start-Service NickERP_Inspection_Web
Start-Service NickERP_Portal
```

Per [`01-deploy.md`](01-deploy.md) §6, watch `/healthz/ready` for
each host to return `Healthy` within 5 s. The five Postgres-touching
checks (`postgres-platform-identity`, `postgres-platform-audit`,
`postgres-inspection`, etc.) should all be Healthy on the new
primary.

### 7.7 Re-verify §6.4 (writes succeed against the new primary)

Same pattern as §6.4. The insert should succeed on the new primary;
fail on the old primary (which is now the unreachable / dead node).

### 7.8 Aftermath

Failover is incident-grade — file a postmortem (template in §11)
and start §8 (failback or rebuild) as the next item, not as a
separate Sprint.

## 8. Failback — original primary becomes standby (or stays standby)

After §7's promotion, the old primary cannot just be "turned back
on" — the timeline diverged at the moment of promote. The old
primary will refuse to start as a primary against the new primary's
WAL (timeline mismatch). Two paths:

### 8.1 Path A — rebuild old primary as new standby (recommended)

Treat the old primary as a fresh box. Start at §5.5 (full backup of
the **new** primary), then §5.6 (`pg_basebackup` from new primary
into old primary's data dir), then §5.7–§6.4. The old primary's
data dir is wiped in §5.6 and the box becomes the new standby.

The cluster ends in: new-primary (formerly standby) + new-standby
(formerly primary). Same shape as before failover, just with the
roles flipped. This is the **canonical** post-failover state.

### 8.2 Path B — fail back, restoring the original topology

Only if there's an operational reason (e.g. the new primary is on
underspec hardware and was always meant to be the standby). Same
mechanics as a planned failover (§7), but in the reverse direction
and with the roles inverted.

Pre-condition: §8.1 must have completed first — you cannot fail
back to a node that was never re-baselined as a standby.

The procedure: start at §7.1 (confirm new primary is gone — but
in this case, you stop it gracefully), then §7.2–§7.7 with the
roles swapped.

> **Why two failovers in quick succession is risky.** Each
> failover increments the timeline ID. After §8.1 + §8.2, the
> cluster is on timeline 3. Future basebackups from a stale
> backup taken on timeline 1 will refuse to apply WAL across the
> divergence point. Document each timeline change in the
> postmortem so the next operator doesn't get caught.

### 8.3 Use case "old primary is unrecoverable"

Skip §8 entirely; live with the new-primary as the only node until
a replacement standby box is provisioned. Replication-lag alerts
will fire (no standby = no streaming consumer). Mute them
explicitly until §8.1 lands; do not just dismiss the alert each
time.

## 9. Verification — post-failover (or post-stand-up)

The §6 checks all apply post-failover. Add:

### 9.1 Both nodes agree on the new timeline

```bash
psql -U postgres -h $PGPRI_HOST  -d postgres -c "SELECT pg_current_wal_lsn(), pg_walfile_name(pg_current_wal_lsn());"
psql -U postgres -h $PGSTBY_HOST -d postgres -c "SELECT pg_last_wal_replay_lsn();"
# Both should report timeline ID 2 (post first failover) - the WAL
# filename embeds the timeline. If the standby's reported LSN is on
# timeline 1 and the primary's on timeline 2, the standby never
# crossed the divergence and §8.1 will fail. Re-baseline.
```

### 9.2 No replication lag growing monotonically

Re-run §6.3 every 10 s for 60 s. The `apply_lag_time` should
oscillate around a steady value (often near zero on a LAN). A
monotonic climb means the standby cannot keep up — disk / CPU /
network saturation. See §10.

### 9.3 Application has reconnected

`/healthz/ready` Healthy on every app host; `pg_stat_activity` on
the new primary shows `usename = nscim_app` rows from each host.

## 10. Monitoring — recommended alerts

These are the v0 minimum set. Wire them into Seq / your alerting
layer; the queries are cheap.

### 10.1 Replication-lag alert

**Threshold:** `apply_lag_time > 60 s`. Fires P2.

**Query (run on standby):**

```sql
SELECT now() - pg_last_xact_replay_timestamp() AS apply_lag_time;
```

A 60 s lag at v0 cardinality means the standby will be 60 s behind
on failover — acceptable. A sustained lag > 5 min means the
standby is effectively non-functional for HA; escalate.

### 10.2 Standby-disconnected alert

**Threshold:** `pg_stat_replication` returns 0 rows on the primary.
Fires P2.

**Query (run on primary):**

```sql
SELECT count(*) FROM pg_stat_replication;
```

A standby that has disconnected does not auto-reconnect if the slot
was dropped or the credentials rotated. Investigate via standby's
log; the canonical recovery is "fix auth / network, restart standby."

### 10.3 Replication-slot disk-fill warning

**Threshold:** `pg_replication_slots.confirmed_flush_lsn` not
advancing for > 1 h while WAL is being written. Fires P2 (P1 if
disk is < 80% full).

**Query (run on primary):**

```sql
SELECT slot_name, active, restart_lsn, confirmed_flush_lsn,
       pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn) AS retained_bytes
FROM pg_replication_slots;
```

A slot whose `restart_lsn` lags the current LSN by more than the
free-disk threshold will fill the WAL volume. Drop the slot in
emergency only — losing the slot means re-baselining the standby.

## 11. Aftermath

### 11.1 Postmortem template (mandatory for failover, optional for stand-up)

```
## HA failover: <YYYY-MM-DD HH:MM> - <trigger>
- Trigger: primary-down | standby-rebuild | planned | other
- Old primary node:  <host> (last-known LSN: <lsn>)
- Old standby node:  <host> (promoted at: <ts>, replay LSN: <lsn>)
- New cluster timeline: <n>
- App-visible downtime (writes failing): <seconds>
- Connection strings updated on: <list of services>
- WAL gap (if any) between old-primary commit and new-primary
  promotion: <bytes / "unknown - primary unreachable">
- Followups filed: <CHANGELOG.md / open-issue links>
- Failback path planned: <8.1 | 8.2 | none>
```

### 11.2 Who to notify

Single-engineer system today: capture the failover in
`CHANGELOG.md` ("HA failover on 2026-MM-DD: primary <X> failed,
promoted <Y>") and update any open issue. Failover that lasted
more than the §2 window is its own incident.

## 12. References

- `ROADMAP.md` §1 (locked answer 3) — the operational shape this
  runbook delivers.
- [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
  — backups + PITR; §5 is the prerequisite step before §5.6 here.
- [`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md)
  — the PG17 lock + upgrade procedure; §3 there is the prerequisite
  for §1 here.
- [`01-deploy.md`](01-deploy.md) §6 — application-host verification
  pattern (`/healthz/ready`); reused in §7.6 + §9.3.
- [`02-secret-rotation.md`](02-secret-rotation.md) §5.6 — the
  "restore minimal-privilege state" check, mirrored in §6.5.
- [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §4.5
  — the mandatory-backup-before-DDL pattern, mirrored in §5.5.
- v1 reference — `C:\Shared\NSCIM_PRODUCTION\` runs single-host
  Postgres today; it is **not** a precedent for HA. The HA shape is
  v2-only.
- [PostgreSQL 17 docs — High Availability, Load Balancing, and
  Replication](https://www.postgresql.org/docs/17/high-availability.html)
  — upstream reference for streaming replication primitives. v0
  uses the "Log-Shipping Standby Servers" + "Streaming Replication"
  configuration; sections 27.2 + 27.4.

