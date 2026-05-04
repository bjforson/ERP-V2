# Runbook 11 — PostgreSQL 17 version lock + upgrade-from-older procedure

> **Scope.** Documents v2's lock to **PostgreSQL 17** per ROADMAP §1
> answer 3 (2026-05-02): "locked to PostgreSQL 17." Covers the
> rationale, the per-node version-verification check, the
> `pg_upgrade` flow for moving an older instance (PG14 / PG15 /
> PG16) to PG17, the v2 compatibility checklist (extensions used,
> catalog views referenced), and the rollback shape.
>
> **PG17 is the lock.** No PG18 evaluation in this sprint. When the
> upstream community ships PG18 (typical Q3 release cadence) and our
> dependencies (Npgsql, EF Core 10 Postgres provider, pgbackrest 2.x)
> support it, the lock is reopened — but that's a separate ROADMAP
> answer revision, not a runbook step.
>
> **Sister docs:**
> - [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) §3 — "are
>   both nodes on the same PG major version?" — points at this
>   runbook for the upgrade path.
> - [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
>   — pgbackrest stanzas pin to a PG major version; a `pg_upgrade`
>   needs the stanza re-stanzaed (§5).
> - [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §4.5
>   — mandatory-backup-before-DDL pattern; same applies before any
>   `pg_upgrade` run.
> - `ROADMAP.md` §1 (locked answer 3) — the PG17 lock.

---

## 1. Symptom

You're verifying the lock or upgrading. There is no symptom — this
runbook is a **planned change** path. Use it when:

- Standing up a new node and you need to confirm it's PG17 before
  starting [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md). The
  §3 check below answers "yes / no."
- Inheriting an older Postgres instance (a PG14 / PG15 / PG16 box
  that was set up before the lock) and needing to bring it to PG17
  before running it as a v2 cluster member.
- Onboarding a new operator and they need to know "why PG17, why
  not just upstream?" — §2 is the explanation.
- Auditing the existing cluster's version posture (e.g. as part of
  Phase V lightweight pre-pilot security audit) — §3 + §5
  produces the per-node report.

If you're hitting a runtime error like
`ERROR:  function pgcrypto.gen_random_uuid() does not exist`, you
might be on a PG version that predates pgcrypto being default —
investigate via §6.4 (compatibility checklist), then upgrade if
needed.

## 2. Why PG17 (the lock rationale)

The locked answer 3 doesn't say *why* PG17 specifically — that's
in this runbook. The reasons:

### 2.1 Features v2 uses

- **`MERGE` statement (PG15+; refined in PG17).** EF Core 10's
  Npgsql provider has improved MERGE support; future bulk
  upsert paths in NickFinance ledger code rely on this.
- **Improvements to `gen_random_uuid()` (PG13+, default-on PG13).**
  Sprint 12 R3 inference-modernization migrations use
  `pgcrypto.gen_random_uuid()` for synthetic UUID PKs in scanner
  threshold profiles + ICUMS signing keys. PG17 has this on by
  default (the `pgcrypto` extension is implicitly included in
  pre-installed extensions).
- **Partition pruning + `EXPLAIN` improvements (PG14+).** Useful
  once `audit.events` partitioning lands (currently a single
  table; deferred-action partitioning is on the followup list).
- **Logical replication shape stability (PG16+).** Not used in v2
  today — but if a future CDC pipeline lands, the v0 lock should
  not be a blocker.
- **`pg_stat_io` system view (PG16+).** Used by Phase V perf
  diagnostics; baked into runbook 09 §10's monitoring queries when
  they evolve.
- **Better incremental sort + parallel queries (PG17).** Generic
  perf wins; no specific v2 code is gated on them.

### 2.2 LTS posture

PG17 is a long-term-supported community release (community policy:
5-year support window, ending Nov 2029). v2's pilot timeline of
6-9 months plus 1-2 year early-life buys 4+ years of upstream
patch coverage on PG17 without a forced upgrade.

The next major version, PG18, ships in late 2026 or 2027 (community
cadence). v2's pilot commitment doesn't intersect that release;
revisiting the lock is post-pilot work.

### 2.3 Tooling support

| Dependency | PG17 support |
|---|---|
| Npgsql (EF Core's Postgres ADO.NET driver) | Supported since 8.0; v2 uses 9.x |
| EF Core 10 Postgres provider | Supported |
| pgbackrest 2.50+ | Supported |
| pg_upgrade in PG17 | Can upgrade FROM PG12+, the FROM-PG10 / 11 paths require an intermediate |
| psql 17 | Backwards-compatible with PG14/15/16 servers |

No v2 dependency requires a sub-PG17 version. The lock has no
forced-downgrade pressure.

### 2.4 Why not "track upstream"

A "use the latest PG" floating posture means the operational
runbooks 09 / 10 / 11 must accommodate version-skew during
upgrades. v0's deploy cadence is multi-week, not nightly; the lock
is a complexity-budget choice. Loosen post-pilot if tooling /
operator team bandwidth allows.

## 3. Verifying version on each cluster node

Use this at every cluster touchpoint — fresh stand-up
([`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) §3),
inheriting an existing box, before a `pg_upgrade`, after a planned
upgrade.

### 3.1 Per-node version check

```bash
psql -U postgres -h <host> -d postgres -c "SELECT version();"
```

Expected output for a PG17 node:

```
PostgreSQL 17.x on x86_64-pc-linux-gnu, compiled by gcc (...) 11.x.x, 64-bit
```

Or on Windows:

```
PostgreSQL 17.x on x86_64-windows, compiled by Visual C++ build (...), 64-bit
```

The leading `17.x` is the only required match. The OS / compiler
suffix differs between hosts and is purely informational.

### 3.2 Node-by-node sweep (HA cluster)

For a primary + standby pair:

```bash
PGPRI_HOST="<primary-ip-or-hostname>"
PGSTBY_HOST="<standby-ip-or-hostname>"

for h in $PGPRI_HOST $PGSTBY_HOST; do
  echo "=== $h ==="
  psql -U postgres -h $h -d postgres -c "SELECT version();" 2>&1 | head -3
done
```

Expected: both rows start with `PostgreSQL 17.x`.

A mismatch is a **stop**: streaming replication does not cross
major versions ([`09-postgres-ha-setup.md`](09-postgres-ha-setup.md)
§3). Upgrade the older node first via §5; do not try to "limp along."

### 3.3 Cluster's binary version (ops-side)

The above queries the running server. To confirm the binaries on
disk match (relevant after a `pg_upgrade`):

```bash
# Linux:
/usr/lib/postgresql/17/bin/postgres --version
/usr/lib/postgresql/17/bin/psql --version

# Windows:
& "C:\Program Files\PostgreSQL\17\bin\postgres.exe" --version
```

Both should report 17.x. If `postgres --version` reports a
different major than `SELECT version()` returns, you have a
running server using older binaries — do **not** ignore this; it's
the symptom of an aborted `pg_upgrade`.

## 4. Pre-upgrade checklist (before §5)

For an inherited PG14 / PG15 / PG16 instance, do all of these
**before** starting the `pg_upgrade` flow.

### 4.1 Confirm the source major version supports a direct path to PG17

`pg_upgrade` to PG17 supports PG12+ as a source. If the source is
PG10 or PG11, do an intermediate upgrade (PG10 → PG13 → PG17 or
similar). v2 expects nothing older than PG14 in any case; a PG10
inherited box is a P3 ("we have a problem before we can even start
this runbook").

```bash
psql -U postgres -d postgres -c "SHOW server_version_num;"
# Expected: 1[2-7]xxxx (e.g. 140012 for PG14.12, 170002 for PG17.2)
# < 120000 (PG12) means a multi-step upgrade is required first.
```

### 4.2 Confirm both PG versions are installed on the host

`pg_upgrade` runs on the source host and needs both the source PG's
binaries (running) and the target PG's binaries (installed but not
running) available simultaneously.

**Debian / Ubuntu:**

```bash
sudo apt install -y postgresql-17 postgresql-17-server-dev
```

This installs PG17 alongside the existing PG14/15/16; both bin
directories coexist.

**RHEL / Fedora:**

```bash
sudo dnf install -y postgresql17-server postgresql17-contrib
```

Confirm the install dirs:

```bash
ls -d /usr/lib/postgresql/14 /usr/lib/postgresql/17 2>&1
# Expected: both directories exist.
```

### 4.3 Disk space — `pg_upgrade --link` vs `--copy`

| Mode | Disk required | Notes |
|---|---|---|
| `--copy` (default) | 2x source PGDATA | Safe; old PGDATA is preserved. v0 default. |
| `--link` | 1.05x source PGDATA | Hardlinks instead of copies; faster. **Old PGDATA is unusable post-upgrade** — rollback requires backup restore. |

v0 chooses `--copy` for the safety margin. `--link` is acceptable
for very large clusters where `--copy` would exceed disk budget,
but the rollback shape becomes "restore from §6 backup" instead
of "stop new, restart old" — capture the choice in §7
postmortem.

### 4.4 Run `pg_upgrade --check` (dry-run)

The `--check` flag runs every compatibility check without modifying
either cluster. Run it before §5 to surface incompatibilities while
they're still cheap to fix.

```bash
sudo systemctl stop postgresql@17-main 2>/dev/null  # ensure target not running
# Source (e.g., 14) keeps running.

sudo -u postgres /usr/lib/postgresql/17/bin/pg_upgrade \
  --old-bindir=/usr/lib/postgresql/14/bin \
  --new-bindir=/usr/lib/postgresql/17/bin \
  --old-datadir=/var/lib/postgresql/14/main \
  --new-datadir=/var/lib/postgresql/17/main \
  --check
```

Common `--check` failures:
- "could not find function `<x>` in old cluster" — extension or
  catalog object that PG17 has dropped. Fix by dropping/installing
  in the source first.
- "incompatible data type usage" — a column is using a type that
  changed semantics. Audit + alter the source first.
- "user-defined unsafe extension" — typically a
  no-longer-shipped extension. Investigate.

A clean `--check` is the green light to do §5.

### 4.5 Mandatory backup

Per [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §4.5:
take a verifiable backup of the source cluster before `pg_upgrade`.
Once pgbackrest is in production
([`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
§5), use `pgbackrest --stanza=nickerp --type=full backup`. Otherwise
fall back to `pg_dump -Fc` — slower but workable for v0 dev cluster
sizes.

The backup is the **only** rollback path from §5; `pg_upgrade --copy`
preserves the old PGDATA but a partial-upgrade can leave both in an
unbootable state.

## 5. The `pg_upgrade` procedure

> **Hard rule.** `pg_upgrade` runs as the `postgres` OS user
> (mirroring `nscim_app` posture from
> [`02-secret-rotation.md`](02-secret-rotation.md): the runtime app
> role is not a superuser; one-off DDL-grade work runs as
> `postgres`).

### 5.1 Stop the source cluster

```bash
# Linux (Debian/Ubuntu):
sudo systemctl stop postgresql@14-main
sudo systemctl status postgresql@14-main          # expected: inactive (dead)

# Linux (RHEL/Fedora):
sudo systemctl stop postgresql-14
sudo systemctl status postgresql-14

# Windows:
Stop-Service postgresql-x64-14
```

Verify nothing is listening on `5432/tcp`:

```bash
nc -vz 127.0.0.1 5432
# Expected: connection refused.
```

### 5.2 Initialise the target cluster (only if not already done)

A fresh PG17 install creates an empty cluster at install time. If
the cluster directory is empty, init it:

```bash
# Debian/Ubuntu:
sudo pg_createcluster 17 main

# RHEL/Fedora:
sudo /usr/pgsql-17/bin/postgresql-17-setup initdb
```

The target cluster must be **stopped** before `pg_upgrade` runs.
Confirm:

```bash
sudo systemctl status postgresql@17-main
```

`pg_upgrade` writes into the target cluster's data dir; a running
target will refuse the upgrade.

### 5.3 Run `pg_upgrade`

```bash
cd /tmp                                              # run from a writable cwd
sudo -u postgres /usr/lib/postgresql/17/bin/pg_upgrade \
  --old-bindir=/usr/lib/postgresql/14/bin \
  --new-bindir=/usr/lib/postgresql/17/bin \
  --old-datadir=/var/lib/postgresql/14/main \
  --new-datadir=/var/lib/postgresql/17/main \
  --jobs=4 \
  --verbose
```

Flag-by-flag:
- `--jobs=4` — parallelism. Set to (CPU cores - 1) for a
  fast-restore tradeoff. Affects pg_dump / pg_restore phases of the
  upgrade, not the catalog swap.
- `--verbose` — operator-friendly progress; routes to stdout.
- (Default) `--copy` — see §4.3.

Expected wall-clock: a few seconds for catalog work + minutes
proportional to data size for `--copy`. A 50 GB cluster typically
finishes in 10-30 min.

`pg_upgrade` writes logs to its working directory (the `cd /tmp`
above). Capture the full log if anything goes wrong; the postmortem
template in §7 references it.

### 5.4 Apply post-upgrade hand-off

`pg_upgrade` produces three scripts the operator must run:

```bash
# In the working directory where pg_upgrade ran:
ls -la /tmp/*.sql /tmp/*.sh 2>&1
# Typical:
#   analyze_new_cluster.sh    - vacuumdb --analyze-in-stages
#   delete_old_cluster.sh     - rm -rf the old PGDATA (only after
#                                you're confident the new cluster is healthy)
#   update_extensions.sql     - if any installed extensions need ALTER EXTENSION UPDATE
```

Do them in this order:

```bash
# 1. Start the new cluster.
sudo systemctl start postgresql@17-main

# 2. Apply extension updates.
sudo -u postgres psql -d postgres -f /tmp/update_extensions.sql

# 3. Run analyze (mandatory; pg_upgrade leaves planner stats stale).
sudo -u postgres /tmp/analyze_new_cluster.sh

# 4. Confirm everything looks healthy via §6 verification.

# 5. ONLY after §6 passes, delete the old cluster.
sudo /tmp/delete_old_cluster.sh
```

> **Critical.** Do not run `delete_old_cluster.sh` until §6
> verification passes. Once it runs, the old PGDATA is gone and the
> only rollback is from §4.5 backup.

### 5.5 Restore extension presence

If §4.4's `--check` flagged any extensions that don't auto-port,
re-create them on the new cluster:

```bash
sudo -u postgres psql -d nickerp_inspection -c "CREATE EXTENSION IF NOT EXISTS pgcrypto;"
sudo -u postgres psql -d nickerp_platform   -c "CREATE EXTENSION IF NOT EXISTS pgcrypto;"
sudo -u postgres psql -d nickerp_nickfinance -c "CREATE EXTENSION IF NOT EXISTS pgcrypto;"
```

`pgcrypto` is the only extension v2 currently uses (per Sprint 12 R3
migrations). Audit `\dx` on each DB after the upgrade and confirm
the extension list matches the source.

### 5.6 Re-stanza pgbackrest (if pgbackrest is in use)

A pgbackrest stanza is keyed to a PG major version. After
`pg_upgrade` you must:

1. Delete the old stanza:
   ```bash
   sudo -u postgres pgbackrest --stanza=nickerp stanza-delete --force
   ```
2. Update `/etc/pgbackrest/pgbackrest.conf` `pg1-path` to the new
   data dir (typically the same path; verify).
3. Re-stanza-create:
   ```bash
   sudo -u postgres pgbackrest --stanza=nickerp stanza-create
   sudo -u postgres pgbackrest --stanza=nickerp --type=full backup
   ```

The `stanza-delete --force` discards the old stanza's backup
history. **Do not** run this until you have confirmed the new
cluster is healthy and the §4.5 backup is still extant in a
separate location — the stanza-delete burns the bridge.

A more conservative path is to leave the old stanza in place and
add a second stanza for PG17:

```ini
[nickerp-pg14]
pg1-path=/var/lib/postgresql/14/main           # historic; read-only
[nickerp]
pg1-path=/var/lib/postgresql/17/main           # new
```

The PG14 stanza becomes purely-historical (you cannot back it up
because PG14 is no longer running) but the prior backups remain
listable via `pgbackrest --stanza=nickerp-pg14 info`. Aged-out
retention will eventually clean it up; explicit
`pgbackrest stanza-delete` when comfortable.

## 6. Compatibility checklist for v2

After §5 finishes, audit these items.

### 6.1 Required extensions

```bash
sudo -u postgres psql -d nickerp_inspection -c "\dx"
sudo -u postgres psql -d nickerp_platform   -c "\dx"
sudo -u postgres psql -d nickerp_nickfinance -c "\dx"
```

Expected on each: at minimum `plpgsql` (built-in) and `pgcrypto`.
If `pgcrypto` is missing on a DB that uses it, §5.5 didn't run for
that DB; install before bringing the host up.

### 6.2 Catalog views referenced by v2 code

These are queried by `tools/migrations/sprint-13-deploy/*.sql`,
the runbook 02 / 09 / 10 diagnostic queries, and EF Core's
`Database.Migrate()` introspection:

| View | Used by | PG17 status |
|---|---|---|
| `pg_database` | runbook 07 §4.1 | unchanged |
| `pg_roles` | runbooks 02 / 07 / 09 | unchanged |
| `pg_stat_activity` | runbooks 02 / 07 | column added in PG17 (`backend_xid_age`); existing columns unchanged |
| `pg_stat_replication` | runbook 09 §6.1 | unchanged |
| `pg_replication_slots` | runbook 09 §10.3 | unchanged |
| `pg_stat_archiver` | runbook 10 §10.2 | unchanged |
| `pg_policies` | runbook 07 §4.3 | unchanged |
| `pg_class` + `pg_namespace` | runbook 07 §6.3 | unchanged |
| `information_schema.tables` | runbook 07 §5.5 | unchanged |
| `__EFMigrationsHistory` | EF Core | not affected by PG version |

No catalog-view churn that affects v2's runbook queries between
PG14 and PG17. The sole add (`backend_xid_age` in `pg_stat_activity`)
is additive; existing column-list `SELECT *`s gain a column,
explicit-column queries are unchanged.

### 6.3 RLS policy syntax (Sprint 13 baseline)

PG17 supports the same RLS policy syntax as PG14+. The
`tenant_isolation_*` policies installed by Sprint 13 use:

- `FORCE ROW LEVEL SECURITY` (PG13+)
- `current_setting('app.tenant_id', true)` with the missing-key
  fallback (PG10+)
- `USING (...)` and `WITH CHECK (...)` clauses (PG10+)

All supported on PG17 unchanged. `\d+ <table>` should report
`POLICY tenant_isolation` rows after the upgrade matching the
pre-upgrade list from runbook 07 §6.3.

### 6.4 Connection string (Npgsql)

EF Core 10 + Npgsql 9.x speaks the PG17 wire protocol natively.
Connection strings need no changes:

```
Host=<host>;Port=5432;Database=nickerp_inspection;Username=nscim_app;Password=...
```

If the host's startup log shows
`could not negotiate SSL/TLS / unsupported protocol version`, you've
hit a PG17-specific minimum-protocol-version regression — the
recovery is to update Npgsql to ≥ 9.0 (which v2 already pins; see
the v2 root `Directory.Build.props`).

### 6.5 Function / type catalog churn

PG17 deprecates a handful of functions present in PG14 (e.g. some
`xpath`-related XML helpers). v2 doesn't use them. Audit by:

```bash
sudo -u postgres psql -d nickerp_inspection -c "\df xml*"
sudo -u postgres psql -d nickerp_inspection -c "\df xpath*"
# Expected: empty / unchanged from PG14.
```

If new internal NickFinance code starts using XML functions, this
audit reopens; for v0 it's a no-op.

## 7. Rollback to an older PG version

> **Hard rule.** `pg_upgrade` is **not** in-place reversible. There
> is no `pg_downgrade`. The only safe path back is **restore from
> backup** to a fresh older-version cluster.

### 7.1 The recovery shape

If §5 fails halfway:

1. **Old cluster still on disk + intact (default `--copy` mode).**
   Re-init the old cluster service via `pg_ctl start` against the
   old PGDATA. Skip `delete_old_cluster.sh` from §5.4. Confirm the
   old cluster comes up; investigate why `pg_upgrade` failed; retry
   when fixed.
2. **Old cluster's PGDATA is gone (`--link` mode, or
   `delete_old_cluster.sh` already ran).** Restore from §4.5 backup
   into a fresh older-PG cluster. This is a §10 (runbook 10)
   restore — but to a **different PG major version** than the
   running one. pgbackrest restores the PG14 PGDATA into a PG14
   cluster; you cannot restore PG14 backups into a PG17 cluster.

The `--copy` mode is the v0 default (§4.3) precisely because path 1
is recoverable without invoking path 2.

### 7.2 If neither path works — `pg_dump` round-trip

The fallback when both the old cluster and the source backup are
unusable: use a `pg_dump -Fc` from §4.5 (which works against any
PG version) and `pg_restore` it into a fresh cluster. This works
across major versions but loses anything written between the
`pg_dump` and the failed `pg_upgrade`.

### 7.3 Document the rollback decision

Capture in the §8 postmortem:
- Which path was taken (1, 2, or 7.2).
- Wall-clock to first byte restored.
- Data delta lost (if any).
- Root cause of the upgrade failure (so the next attempt can
  avoid it).

A failed `pg_upgrade` is incident-grade; the §8 postmortem is
mandatory.

## 8. Aftermath

### 8.1 Postmortem template (mandatory for upgrade, optional for stand-up verification)

```
## PG17 upgrade: <YYYY-MM-DD HH:MM> - <hostname>
- Source major version: <14 | 15 | 16>
- Target major version: 17
- Mode: --copy | --link
- pg_upgrade --check result: clean | <list of warnings>
- Wall-clock for §5: <minutes>
- Wall-clock for §5.4 analyze: <minutes>
- Extensions re-installed (§5.5): <list>
- pgbackrest re-stanzaed: yes / no / "n/a, pgbackrest not in use"
- Old cluster delete_old_cluster.sh executed at: <ts | "deferred">
- §6 compatibility checklist: <pass per item>
- Anomalies: <list>
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 8.2 Who to notify

Single-engineer system today: capture in `CHANGELOG.md`
("Upgraded host <X> from PG14 to PG17 on 2026-MM-DD"). Update any
ROADMAP / sprint-progress entry tracking the version-lock posture.

## 9. References

- `ROADMAP.md` §1 (locked answer 3) — the PG17 lock.
- [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) §3 — the
  prerequisite "are both nodes on PG17" check; this runbook is the
  upgrade path when one isn't.
- [`10-pgbackrest-backup-restore.md`](10-pgbackrest-backup-restore.md)
  — pgbackrest stanza pinning; §5.6 here re-stanzas after upgrade.
- [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §4.5
  — mandatory-backup pattern, mirrored in §4.5 here.
- [`02-secret-rotation.md`](02-secret-rotation.md) §5.6 — `nscim_app`
  posture; restored verification after the upgrade lands.
- [PostgreSQL 17 docs — Upgrading a PostgreSQL Cluster](https://www.postgresql.org/docs/17/pgupgrade.html)
  — upstream `pg_upgrade` reference.
- [PostgreSQL 17 release notes](https://www.postgresql.org/docs/17/release-17.html)
  — feature list + deprecations between PG16 and PG17.
- [PostgreSQL community version policy](https://www.postgresql.org/support/versioning/)
  — the 5-year LTS posture referenced in §2.2.

