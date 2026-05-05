# Runbook 10 — pgbackrest backup + restore (full + incremental + PITR)

> **Scope.** Configuring and operating **pgbackrest** as v2's
> Postgres backup tool, per ROADMAP §1 answer 3 (2026-05-02):
> "pgbackrest backups (full + incremental + PITR)." Covers initial
> install + stanza setup, the recurring backup cadence (full +
> incremental + continuous WAL archive), restore + point-in-time
> recovery, the quarterly test-restore drill, retention, and the
> alert wiring needed to know when backups silently stop working.
>
> **pgbackrest is the locked tool.** Not `pg_dump`, not WAL-G, not
> Barman. The lock predates this runbook (ROADMAP). Don't introduce
> a parallel backup tool without reopening the locked-answer.
>
> **Sister docs:**
> - [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) — the HA
>   primary + standby pair this runbook backs up. §5.5 of runbook 09
>   forwards to §5 here as a mandatory prerequisite to standby init.
> - [`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md)
>   — pgbackrest's stanza is keyed to a specific PG major version; a
>   `pg_upgrade` flow needs the stanza re-stanzaed.
> - [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §4.5
>   — the precedent for "mandatory backup before destructive change."
>   Replace its `pg_dump` shape with the §5 pgbackrest shape once
>   pgbackrest is in production; the runbook 07 step then becomes "run
>   pgbackrest --type=full backup, confirm with `info`."
> - [`01-deploy.md`](01-deploy.md) — the application deploy mechanics;
>   pgbackrest does not interact with the application except at PITR
>   time (the application stays stopped during a restore).

---

## 1. Symptom

You're standing up backups, taking a scheduled backup, restoring,
or running the quarterly drill. There is no symptom — this runbook
is a planned-change path. Use it when:

- Bringing pgbackrest up for the first time on a primary that has
  never been backed up. Today's `C:\Shared\ERP V2` dev posture has
  no pgbackrest configuration; this runbook installs it.
- Adding a stanza for a new cluster (e.g. a second region).
- Performing the recurring full / incremental cadence — most operator
  interaction is through the §6 cron / scheduled-task wiring, which
  runs `pgbackrest backup` automatically; manual runs only when the
  schedule is being seeded or a one-off is needed.
- Restoring after a P1 incident (data corruption, accidental DROP,
  ransomware, two-node simultaneous failure).
- Running the quarterly test-restore drill (§8) — mandatory; if the
  drill is skipped for two quarters in a row, treat the backups as
  unverified.
- Recovering from a "backups silently failing" alert (§10).

If the cluster is up and a recent backup ran cleanly, you do not
need this runbook for routine operations — pgbackrest is run via
cron / scheduled-task once configured. Use this runbook when you're
**configuring**, **restoring**, or **drilling**.

## 2. Severity

| Trigger | Severity | Response window |
|---|---|---|
| First-time stand-up | n/a — operator-initiated | as scheduled |
| Adding a stanza for a new cluster | n/a — operator-initiated | as scheduled |
| Routine full / incremental run (cron-driven) | n/a — automated | within the cron window; failure pages P2 |
| Backup-failed alert fires | P2 | inside 4 h |
| 7-day-no-successful-backup alert fires | P1 | inside 30 min — RPO is degrading |
| WAL archive command failing (`pg_stat_archiver.failed_count` > 0) | P2 | inside 4 h — PITR window is closing |
| Restore drill (quarterly) | n/a — operator-initiated | as scheduled |
| Live restore (P1 incident: data loss / corruption / cluster destroyed) | P1 | inside 1 h to first byte restored |

The §8 drill is **non-negotiable** at quarterly cadence. A backup
that has never been restored is an aspiration, not a backup.

## 3. Quick triage (60 seconds)

Before you start, answer:

- **Are you configuring or restoring?** §5 is configuration + first
  backup; §7 is restore + PITR; §8 is the drill. Pick the right
  section.
- **Where is the repo?** pgbackrest's repo (the durable backup
  store) is configured per-cluster. v0 default: a separate disk on
  the same host (acceptable for the dev / first-pilot box only).
  Production target: cloud storage (S3 / Azure Blob) or a remote
  rsync target; "same disk as PGDATA" is a common-mode-failure if
  the disk dies. Capture the choice in §4.1.
- **Is the cluster HA?** If both primary + standby are up
  ([`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) finished),
  back up the **primary** only. Backing up a standby works but
  requires `--backup-standby` in the config and is a v2 deferred
  variant; skip for v0.
- **Is anyone else running a backup?** pgbackrest serializes via a
  lock file in the repo, so concurrent runs fail-fast — but a hung
  prior run leaves a stale lock that requires manual cleanup
  (§5.7). Confirm no prior run is in progress.
- **Has §3 of runbook 11 (PG17 confirmation) been done?** pgbackrest
  stanzas are pinned to a major version. Confirming PG17 first
  prevents stanzaing against PG14 and then needing to re-stanza.

## 4. Diagnostic commands

All commands assume bash. Linux is the canonical host for pgbackrest
(upstream packages are Linux-first); the Windows variant is documented
in §5.1 but is best-effort. Many real deployments run pgbackrest on
the Postgres host itself (so it can read `PGDATA` directly); cross-host
backups via SSH are a more complex topology, deferred for v0.

### 4.1 Confirm the repo location + free space

```bash
# Where is the repo? (Read from config; section 5.2 sets it.)
PGBACKREST_REPO=/var/lib/pgbackrest        # Linux default
# PGBACKREST_REPO="C:\pgbackrest"          # Windows
df -h "$PGBACKREST_REPO"
# Expected: enough free space for 90 days of full-weekly +
# 30-days-of-incremental + 14-days-of-WAL.
```

A v0 sizing rule of thumb: **3x the live `PGDATA` size**. With
weekly fulls + 6-hourly incrementals + continuous WAL, a 50 GB
cluster needs ~150 GB free in the repo. Tune up if your cluster
is write-heavy.

### 4.2 Confirm pgbackrest is installed + sees the cluster

```bash
pgbackrest --version
# Expected: pgBackRest 2.50 or newer (as of 2026-05-04 the released
# series is 2.x, with 2.50+ supporting PG17). v0 minimum: 2.50.

pgbackrest --stanza=nickerp info 2>&1
# Expected (post-stanza): cluster info block.
# Pre-stanza: "ERROR: stanza 'nickerp' is not found"
# - this is fine on a fresh stand-up; §5 creates it.
```

If `pgbackrest` is not on PATH, `which pgbackrest` and the install
section §5.1 cover the install.

### 4.3 Confirm WAL archiving is being attempted

The primary's `archive_command` should reference pgbackrest. From
runbook 09 §5.1, that command is
`pgbackrest --stanza=nickerp archive-push %p`. Confirm:

```bash
psql -U postgres -d postgres -c "SHOW archive_command;"
psql -U postgres -d postgres -c "SHOW archive_mode;"
# Expected: archive_command starts with 'pgbackrest', archive_mode = on.
```

And confirm the archiver isn't failing:

```bash
psql -U postgres -d postgres -c \
  "SELECT archived_count, failed_count, last_archived_wal,
          last_failed_wal, last_failed_time
   FROM pg_stat_archiver;"
# A non-zero failed_count means archive-push has been failing.
# Fix that BEFORE running another backup - the backup metadata
# refers to the WAL it expects to be in the archive.
```

### 4.4 Snapshot existing backups

```bash
pgbackrest --stanza=nickerp info
```

The output lists all stored backups (full + incremental), with
timestamps + LSNs. Save this as the BEFORE snapshot for any
postmortem; the §6 retention enforcement may have pruned older
backups since the last `info` you saw.

### 4.5 Verify the restore target (for §7 only)

If you're restoring, the target host needs:
- pgbackrest installed at the same major version as the source
  cluster's pgbackrest.
- Enough disk for the restored cluster (≥ source PGDATA size).
- Postgres 17 installed but **not** running (`PGDATA` will be
  rewritten).
- Network reachability to the repo (or a local copy of the repo if
  the repo was on the destroyed primary's disk).

## 5. Resolution — first-time stand-up

This is a one-time path. After §5 finishes, ongoing backups run via
§6's cron / scheduled-task wiring; operator interaction is via §4
diagnostics and §10 alert response.

### 5.1 Install pgbackrest

**Debian / Ubuntu:**

```bash
sudo apt update
sudo apt install -y pgbackrest
pgbackrest --version
```

**RHEL / Fedora:**

```bash
sudo dnf install -y pgbackrest
pgbackrest --version
```

**Windows:** pgbackrest is **Linux-first**. The supported v0 path
on a Windows-only prod box is "run pgbackrest in a WSL2
distribution that mounts `C:\Program Files\PostgreSQL\17\data`."
The Windows-native build of pgbackrest exists upstream but is not
on the same release cadence as the Linux build; treat it as
unsupported for v0. Capture the WSL2 path in the §11 postmortem so
the operator knows what they're running.

If the production box is dual-boot or virtualised: prefer running
the Postgres cluster on Linux. If the cluster must be Windows for
licensing or domain reasons, the cleaner path is "Postgres on
Windows, pgbackrest on a separate Linux backup host that pulls via
SSH" — see **[§5A](#5a-production-posture-for-windows-host-postgres)**
for the full posture-decision (recommended / acceptable-v0 /
acceptable-v1) and the SSH topology details. Pilot deployment on a
Windows host MUST pick one of the §5A shapes before §5.4 runs.

### 5.2 Configure `/etc/pgbackrest/pgbackrest.conf`

Write the global + stanza configuration. v0 minimum:

```ini
[global]
repo1-path=/var/lib/pgbackrest
repo1-retention-full=12
repo1-retention-diff=4
repo1-retention-archive=14
repo1-retention-archive-type=incr
log-level-console=info
log-level-file=detail

start-fast=y
delta=y

[nickerp]
pg1-path=/var/lib/postgresql/17/main
pg1-port=5432
pg1-user=postgres
```

Key knobs:
- `repo1-path` — the durable backup store. v0 default is local
  disk; production target is a cloud-backed path
  (`repo1-type=s3` + `repo1-s3-*` keys; see §5.6).
- `repo1-retention-full=12` — keep 12 most-recent full backups.
  At weekly fulls, that's ~84 days of fulls.
- `repo1-retention-diff=4` — keep 4 most-recent differential
  backups (only relevant if differentials are scheduled; §6 v0
  uses incrementals, not differentials, so this is mostly a
  belt-and-braces value).
- `repo1-retention-archive=14` + `archive-type=incr` — keep 14
  days of WAL archive following the most-recent incremental.
  This is the **PITR window**.
- `start-fast=y` — issue an immediate checkpoint on backup start;
  trades a brief I/O spike for a much shorter "backup is running"
  window.
- `delta=y` — restore copies only changed files (faster for
  in-place restores after a partial corruption).

### 5.3 Configure the Postgres side

The relevant `postgresql.conf` keys are already in
[`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) §5.1
(`archive_mode = on` and `archive_command = 'pgbackrest --stanza=nickerp archive-push %p'`)
— if runbook 09 has been run, this is already in place. Verify:

```bash
psql -U postgres -d postgres -c "SHOW archive_command;"
# Expected: pgbackrest --stanza=nickerp archive-push %p
```

If `archive_mode = off` or the command doesn't reference pgbackrest,
edit `postgresql.conf` and restart Postgres. Without an active
archive_command, pgbackrest backups still work but PITR breaks
(WAL between backups isn't captured).

### 5.4 Create the stanza + first full backup

```bash
# 1. Create the stanza. Idempotent; safe to re-run.
sudo -u postgres pgbackrest --stanza=nickerp stanza-create

# 2. Run a check - confirms config + Postgres connection +
#    archive_command are wired.
sudo -u postgres pgbackrest --stanza=nickerp check
# Expected: "INFO: stanza 'nickerp' check: completed successfully"

# 3. First full backup. Takes O(GB) minutes - one-time cost.
sudo -u postgres pgbackrest --stanza=nickerp --type=full backup

# 4. Verify the backup is recorded.
sudo -u postgres pgbackrest --stanza=nickerp info
```

`pgbackrest info` output for a healthy first-backup state:

```
stanza: nickerp
    status: ok
    cipher: none

    db (current)
        wal archive min/max (17): 000000010000000000000001/0000000100000000000000XX

        full backup: 20260504-120000F
            timestamp start/stop: 2026-05-04 12:00:00 / 2026-05-04 12:01:30
            wal start/stop: 000000010000000000000003 / 000000010000000000000005
            database size: 50.0MB, database backup size: 50.0MB
            repo1: backup set size: 12.5MB, backup size: 12.5MB
```

`status: ok` is the sanity-check; anything else is a §10 alert
condition.

### 5.5 Verify with a test restore (one-time, before declaring done)

A first backup that has never been restored is unverified. Skip
to §8 and do the drill once now — same procedure, just the first
time.

### 5.6 Optional — point the repo at S3 / Azure Blob

For a production-grade repo, replace the `repo1-path` block in §5.2
with a cloud-backed config:

```ini
[global]
repo1-type=s3
repo1-path=/erp-v2/pgbackrest
repo1-s3-bucket=<bucket-name>
repo1-s3-endpoint=s3.<region>.amazonaws.com
repo1-s3-region=<region>
repo1-s3-key=<access-key>
repo1-s3-key-secret=<secret-key>
repo1-storage-verify-tls=y
repo1-cipher-type=aes-256-cbc
repo1-cipher-pass=<random-32-char-string>
```

`repo1-cipher-pass` enables at-rest encryption inside the repo
(pgbackrest-side, on top of S3 server-side encryption). Capture the
cipher password in the operator's password manager — losing it is
"backups are unrecoverable encrypted bytes."

Same shape for Azure Blob (`repo1-type=azure` + the corresponding
`-azure-*` keys); see [pgbackrest user
guide](https://pgbackrest.org/user-guide.html) §"Repository Storage."

### 5.7 Recovering from a stale lock

If a prior `pgbackrest backup` was killed mid-run, the lock file in
the repo prevents the next run. Symptom:

```
ERROR: [050]: unable to acquire lock on file '/var/lib/pgbackrest/lock/nickerp-backup.lock': Resource temporarily unavailable
```

Check the lock file's mtime; if it's older than the longest credible
backup duration (1-2 hours for v0 cluster sizes) and no
`pgbackrest` process is running, manually remove it:

```bash
sudo ls -la /var/lib/pgbackrest/lock/
sudo pgrep -fa pgbackrest             # confirms no live pgbackrest
sudo rm /var/lib/pgbackrest/lock/nickerp-backup.lock
```

Re-run the backup. **Do not** force-remove the lock if pgbackrest
is actually still running — the resulting concurrent write to the
repo can corrupt manifests.

## 5A. Production posture for Windows-host Postgres

> **Scope.** Pilot is locking on Kotoka (KIA Cargo) or Takoradi per
> plan §13. Both candidate sites currently run NSCIM v1 on Windows
> Server, and the v2 deployment will inherit that host shape unless
> we move the Postgres cluster to Linux at pilot lock. This section
> documents the three Windows-host postures pgbackrest can run under
> and which one is recommended for production.
>
> **Read this BEFORE §5.4 if pilot is on Windows.** The posture
> choice changes pgbackrest install + invocation + scheduling; it
> does not change the §6 cadence or the §7 PITR shape.

The Sprint 27 followup `FU-windows-pgbackrest-posture` flagged §5.1's
"WSL2 is acceptable v0" line as the only Windows guidance in the
runbook. That short line is honest about the v0 trade-off but doesn't
spell out the cleaner long-term shape — this section closes that gap.

### 5A.1 Posture comparison

| Posture | Status | RPO | Recovery time | Operator complexity | Adopt when |
|---|---|---|---|---|---|
| **Postgres on Windows + pgbackrest on a separate Linux backup host (SSH)** | **Recommended** | 6 h (matches §6 cadence) | minutes-hours per §7 | medium — needs SSH key mgmt + cross-host firewall | pilot is Windows AND we have a Linux backup VM available |
| WSL2-on-prod-host pgbackrest | Acceptable v0 | 6 h | matches recommended path | low — single host | dev box / first-pilot blast-radius / no Linux backup VM yet |
| Native Windows pgbackrest binary | Acceptable v1 (future) | 6 h | matches recommended path | low — single host, no WSL | upstream ships a supported Windows release on the same cadence as the Linux build |

The recommended path is the cleaner long-term shape because:
- It isolates the backup tool from the production host's failure
  modes (host disk dies → repo on a different machine survives).
- WSL2 has historically been an awkward production dependency
  (auto-shutdown when the parent Windows session ends, tricky
  service-account integration, kernel upgrades that break the
  distro). The Linux backup host is a regular Linux VM that the
  operator's existing tooling already supports.
- pgbackrest's authentication model (SSH key from primary to
  backup host) is well-supported — `pg1-host` + `pg1-host-user` +
  `pg1-host-config` keys in pgbackrest.conf cover the cross-host
  case.

The "v0 acceptable" WSL2 path stays valid for the dev box and for a
first-pilot launch where the operator hasn't yet provisioned the
Linux backup VM. **Do not stay on the WSL2 path past the first
pilot quarter** — the §8 quarterly drill is the moment to migrate to
the recommended shape.

### 5A.2 Recommended — SSH-based Linux backup host

**Topology:**

```
                +-----------------------+
                |  Windows prod host     |          +--------------------+
                |  Postgres 17           |          |  Linux backup VM    |
                |  C:\PostgreSQL\17\data |          |  pgbackrest 2.50+   |
                |  ssh-server installed  |  --SSH-->|  /var/lib/pgbackrest|
                |  pgbackrest user       |  (rsync) |                     |
                +-----------------------+          +--------------------+
```

**One-time setup:**

1. **Install pgbackrest on the Linux backup VM** per §5.1 (Debian /
   RHEL package). The Windows host gets **no** pgbackrest install —
   only OpenSSH server.

2. **Provision an SSH service account on the Windows host** that
   pgbackrest can use to read `PGDATA`:

   ```powershell
   # Run as Administrator on the Windows host.
   $secret = Read-Host -AsSecureString "Service account password"
   New-LocalUser  -Name pgbackrest -Password $secret -PasswordNeverExpires `
                  -UserMayNotChangePassword -Description "pgbackrest read-only PGDATA"
   Add-LocalGroupMember -Group "Backup Operators" -Member pgbackrest
   icacls "C:\Program Files\PostgreSQL\17\data" /grant "pgbackrest:(OI)(CI)R" /T
   ```

   The "Backup Operators" group + recursive read grant lets the
   account read `PGDATA` without write privileges.

3. **Install OpenSSH server on the Windows host** (Server 2022 / W11
   ship with it as an optional feature):

   ```powershell
   Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
   Start-Service sshd
   Set-Service -Name sshd -StartupType Automatic
   New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server (sshd)' `
                       -Enabled True -Direction Inbound -Protocol TCP `
                       -Action Allow -LocalPort 22
   ```

4. **Generate an SSH key pair on the Linux backup VM**, install the
   public half on the Windows host:

   ```bash
   # On the Linux backup VM (run as the pgbackrest user, NOT root).
   sudo -u pgbackrest ssh-keygen -t ed25519 -f ~/.ssh/pgbackrest_id -N ""
   ```

   Copy `~/.ssh/pgbackrest_id.pub` content to the Windows host's
   `C:\Users\pgbackrest\.ssh\authorized_keys`. Ensure `authorized_keys`
   has the right ACLs — Windows OpenSSH is fussy:

   ```powershell
   icacls "C:\Users\pgbackrest\.ssh\authorized_keys" `
       /inheritance:r /grant "pgbackrest:(R)" /grant "SYSTEM:(F)"
   ```

5. **Configure pgbackrest.conf on the Linux backup VM** with the
   Windows host's Postgres path:

   ```ini
   [global]
   repo1-path=/var/lib/pgbackrest
   repo1-retention-full=12
   repo1-retention-diff=4
   repo1-retention-archive=14
   repo1-retention-archive-type=incr
   log-level-console=info
   log-level-file=detail
   start-fast=y
   delta=y

   # Cross-host: tell pgbackrest the cluster lives on the Windows host.
   [nickerp]
   pg1-host=windows-prod.example.internal
   pg1-host-user=pgbackrest
   pg1-host-config=/etc/pgbackrest/pgbackrest.conf
   pg1-path=/c/Program Files/PostgreSQL/17/data
   pg1-port=5432
   pg1-user=postgres
   ```

   Note `pg1-path` uses the Linux-side translation of the Windows
   path — pgbackrest's SSH transport reads files via the OpenSSH
   server, which exposes `C:\` as `/c/`.

6. **Configure the Postgres `archive_command` on the Windows host**
   to push WAL to the Linux backup VM via SSH:

   ```ini
   # postgresql.conf on the Windows host
   archive_mode = on
   archive_command = 'ssh pgbackrest@linux-backup.example.internal "pgbackrest --stanza=nickerp archive-push" < %p'
   ```

   The trailing `< %p` redirects the WAL file content over stdin to
   the remote `archive-push` invocation. This makes WAL archiving
   another SSH round-trip per WAL switch — typically 1-2/min on a
   write-heavy DB; fine over a LAN, watch latency over a WAN link.

7. **First backup + verify:** §5.4 procedure runs unchanged from the
   Linux backup VM:

   ```bash
   sudo -u pgbackrest pgbackrest --stanza=nickerp stanza-create
   sudo -u pgbackrest pgbackrest --stanza=nickerp check
   sudo -u pgbackrest pgbackrest --stanza=nickerp --type=full backup
   ```

   The check command ssh's into the Windows host, confirms the
   archive_command is wired, and runs a one-shot WAL push to verify
   the round-trip.

### 5A.3 Acceptable v0 — WSL2 on the prod host

This is the §5.1 path that already exists. Recap, with the cron /
scheduler shape called out:

1. Install a WSL2 distro on the Windows host (Ubuntu 22.04+).
2. Inside the WSL2 distro: `sudo apt install pgbackrest`.
3. Mount `C:\Program Files\PostgreSQL\17\data` into the WSL2 filesystem
   — by default WSL2 exposes `C:\` as `/mnt/c/`, so `pg1-path =
   /mnt/c/Program Files/PostgreSQL/17/data`.
4. Run pgbackrest commands inside WSL2 via `wsl.exe -u postgres
   pgbackrest ...` (see §6.3 for scheduled-task wiring).

**Known failure modes:**
- WSL2 auto-shutdown when no console session is active. Workaround:
  `wsl.exe --set-default-version 2` then leave a `wsl.exe -d Ubuntu
  -- /bin/true` running as a Windows service via NSSM, or use the
  Windows 11 24H2+ "vmIdleTimeout=-1" setting in `.wslconfig`.
- WSL2 distro upgrades occasionally rebuild the kernel and break
  network paths to the Postgres host (localhost:5432) — re-test after
  any WSL kernel update.
- File system performance through `/mnt/c/` is 5-10x slower than
  native Linux ext4. Backup wall-clock at scale will be noticeably
  longer than the recommended path; tolerable for v0 cluster sizes.

### 5A.4 Acceptable v1 — Native Windows pgbackrest

Upstream pgbackrest builds Windows binaries periodically but does NOT
treat them as production-ready (per the Quick Start docs, "the Windows
release lags the Linux release, and several features rely on POSIX
semantics that Windows does not provide identically").

If a future upstream release lifts that caveat, the migration path is:

1. Replace the WSL2 install with the native Windows binary.
2. `pgbackrest.conf` and the `archive_command` switch from
   `wsl.exe -u postgres pgbackrest ...` to `pgbackrest.exe ...`.
3. Scheduled tasks lose the `wsl.exe` wrapper.
4. Re-stanza only required if the major version changes (it
   shouldn't on a same-version binary swap).

Watch upstream release notes ([pgbackrest
releases](https://github.com/pgbackrest/pgbackrest/releases)) for a
"Windows: production-ready" line in the changelog. Until then, treat
this posture as deferred-action.

### 5A.5 Cross-link to runbook 09 (HA setup)

Runbook 09 §5.5 calls "take a pgbackrest backup before initialising
the standby" as a mandatory pre-flight. That step's invocation
changes by posture:

| Posture | §5.5 invocation |
|---|---|
| Recommended (SSH Linux backup host) | `sudo -u pgbackrest pgbackrest --stanza=nickerp --type=full backup` (on the Linux backup VM) |
| WSL2 v0 | `wsl.exe -u postgres pgbackrest --stanza=nickerp --type=full backup` (on the Windows prod host) |
| Native Windows v1 | `pgbackrest.exe --stanza=nickerp --type=full backup` (on the Windows prod host) |

The standby's `pgbackrest archive-push` command (used post-failover
when the standby is promoted) follows the same shape — runbook 09 §8.1
already references runbook 10's `archive-push` line; that line is
posture-aware via the §6.3 / §6.4 wrapper.

## 6. Routine cadence (cron / scheduled-task)

After §5 finishes, daily operations are automated. The operator
seeds these once and reads §10 alerts thereafter.

### 6.1 Recommended schedule

| Cadence | Type | Time (UTC) | Wall-clock cost (v0 cluster) |
|---|---|---|---|
| Weekly | full | Sunday 02:00 | 2-15 min |
| Every 6 h | incremental | 00:00, 06:00, 12:00, 18:00 | 1-3 min |
| Continuous | WAL archive-push | per WAL switch | < 1 s per WAL file |

Why this shape:
- Full + incremental + continuous is the documented v0 default —
  PITR can rewind to any second within the 14-day archive window.
- Weekly fulls keep restore times tractable (a restore replays the
  fulls + incrementals + WAL since; ≤ 7 days of incrementals to
  replay is bounded).
- 6-hour incrementals = RPO of 6 h **without** WAL replay; with
  WAL replay (PITR) RPO is ≤ 1 minute.

### 6.2 Cron (Linux native, OR Linux backup host per §5A.2)

Same shape regardless of whether the cluster lives on the same host
or pgbackrest is reaching across SSH per §5A.2. The cross-host
config in pgbackrest.conf does the cross-host work; the cron stanza
is unchanged.

```cron
# /etc/cron.d/pgbackrest

# Full backup, Sunday 02:00 UTC.
0 2 * * 0 postgres pgbackrest --stanza=nickerp --type=full backup

# Incremental backup, every 6 hours.
0 0,6,12,18 * * * postgres pgbackrest --stanza=nickerp --type=incr backup
```

Use `MAILTO=<ops-alias>` at the top of the cron file so failures
land in an inbox; alternatively, wrap the commands in a script that
sends to your alerting layer on non-zero exit.

For the §5A.2 SSH posture, the cron file lives on the **Linux
backup VM**, not the Windows prod host. The `archive_command` on the
Windows host runs continuously per WAL switch and is not in cron.

### 6.3 Scheduled tasks (Windows host — both v0 and recommended posture)

The shape depends on the §5A posture:

#### 6.3.1 Recommended posture — SSH-based Linux backup host (§5A.2)

In this posture **the Windows host has no scheduled tasks**. The
Linux backup VM runs the §6.2 cron; all backup logic lives there.
The Windows prod host's only contribution is:

- Continuous WAL archive-push via the `archive_command` configured
  in `postgresql.conf` (already running per §5A.2 step 6).
- The OpenSSH server that the Linux backup VM SSH's into.

Operator audit: `Get-ScheduledTask -TaskName 'pgbackrest-*'` on the
Windows host should return **zero** entries in this posture. If
there are leftover tasks from a prior WSL2 deployment, unregister
them so they don't conflict with the Linux backup VM's runs:

```powershell
Get-ScheduledTask -TaskName 'pgbackrest-*' | `
    Unregister-ScheduledTask -Confirm:$false
```

#### 6.3.2 WSL2 v0 posture (§5A.3)

When pgbackrest is in WSL2, the Windows scheduled task invokes it
via `wsl.exe`. Wrap each invocation in a PowerShell script so the
exit code is captured and routed to the alert layer.

`C:\PgBackRest\Run-Backup.ps1`:

```powershell
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('full', 'incr')]
    [string]$Type
)

$ErrorActionPreference = 'Stop'
$logFile = "C:\PgBackRest\logs\$(Get-Date -Format 'yyyy-MM-dd-HHmm')-$Type.log"
New-Item -ItemType Directory -Path (Split-Path $logFile) -Force | Out-Null

# Use --user so pgbackrest runs under the dedicated postgres role
# inside WSL2 — same posture as a Linux deployment.
$exitCode = (
    Start-Process -FilePath 'wsl.exe' `
        -ArgumentList @('-u', 'postgres', 'pgbackrest',
                        "--stanza=nickerp", "--type=$Type", 'backup') `
        -RedirectStandardOutput $logFile `
        -RedirectStandardError "$logFile.err" `
        -Wait -PassThru -NoNewWindow
).ExitCode

if ($exitCode -ne 0) {
    # Route to alert layer. Replace the Send-MailMessage with whatever
    # the operator's alerting backbone is (Seq, Pushover, PagerDuty).
    Write-Error "pgbackrest $Type backup failed (exit $exitCode); see $logFile"
    exit $exitCode
}

Write-Host "pgbackrest $Type backup OK; log: $logFile"
exit 0
```

`C:\PgBackRest\Register-Tasks.ps1` (run once as Administrator):

```powershell
$scriptPath = 'C:\PgBackRest\Run-Backup.ps1'

# Full backup, Sunday 02:00 UTC.
$fullAction  = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Type full"
$fullTrigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Sunday -At 2am
Register-ScheduledTask -TaskName 'pgbackrest-nickerp-full' `
    -Action $fullAction -Trigger $fullTrigger `
    -User 'SYSTEM' -RunLevel Highest -Force

# Incremental backup, every 6 hours.
$incrAction  = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Type incr"
$incrTrigger = New-ScheduledTaskTrigger -Daily -At 12am
$incrTrigger.Repetition = (New-ScheduledTaskTrigger -Once -At 12am `
    -RepetitionInterval (New-TimeSpan -Hours 6) `
    -RepetitionDuration (New-TimeSpan -Days 365)).Repetition
Register-ScheduledTask -TaskName 'pgbackrest-nickerp-incr' `
    -Action $incrAction -Trigger $incrTrigger `
    -User 'SYSTEM' -RunLevel Highest -Force
```

After registration: `Get-ScheduledTask -TaskName 'pgbackrest-*'` lists
both tasks. `Get-ScheduledTaskInfo -TaskName 'pgbackrest-nickerp-full'`
shows last run + last result; integrate with §10 monitoring.

#### 6.3.3 Native Windows v1 posture (§5A.4 — future)

When upstream pgbackrest ships a production-ready Windows binary,
swap `wsl.exe -u postgres pgbackrest` in `Run-Backup.ps1` for
`pgbackrest.exe`. Otherwise the wrapper script + `Register-Tasks.ps1`
shape stays identical.

### 6.4 Ongoing verification

After cron / scheduled tasks run for a week, `pgbackrest info` should
show ~7 incrementals chained off a single full. The §10 monitoring
alerts fire if this stops being true.

For the §5A.2 recommended posture, run `pgbackrest info` on the
**Linux backup VM** — the Windows host has no pgbackrest install in
that posture. For the §5A.3 / §5A.4 postures, run it on the Windows
host (via `wsl.exe -u postgres pgbackrest --stanza=nickerp info` for
WSL2 or `pgbackrest.exe info` for native).

## 7. PITR restore procedure

Trigger: data loss / corruption — accidental `DROP TABLE`, a bad
migration, ransomware encryption, or two-node failure that took out
the HA cluster (runbook 09 doesn't cover this; this runbook does).
Goal: restore the cluster to a specific point in time, ideally
seconds before the bad event.

**Allowed downtime: O(minutes) for a small cluster, O(hours) for
a large one.** PITR replays WAL — wall-clock time scales with the
WAL volume since the most-recent backup.

### 7.1 Pre-flight — pick the target time

Two flavors of target:
- `--type=time --target='2026-05-04 14:32:00 UTC'` — restore to a
  wall-clock time. Most-common operator path.
- `--type=lsn --target='0/3000148'` — restore to a specific LSN.
  Use when you have an exact LSN from the application's audit log
  or from a replication monitor.

Pick conservatively: a target 1 minute *before* the bad event is
better than 10 ms before. WAL replay finds the recovery target by
scanning forward; over-shooting is fine, under-shooting is fine,
matching to-the-second is unnecessary.

### 7.2 Stop Postgres on the target host

The restore overwrites `PGDATA`. Postgres must not be running:

```bash
# Linux:
sudo systemctl stop postgresql

# Windows (in WSL or via Stop-Service):
Stop-Service postgresql-x64-17

# Confirm nothing is listening on 5432.
nc -vz 127.0.0.1 5432
# Expected: connection refused.
```

If this is a multi-DB cluster, every database in the cluster is
restored — pgbackrest restores the whole cluster, not per-DB.
Capture the implications: a restore to recover one accidentally-
dropped table also rolls back every other DB to that LSN.

### 7.3 Snapshot the current `PGDATA` (just in case)

If the host still has a partially-corrupt `PGDATA`, move it aside
rather than deleting it — it might contain post-target writes you
want to forensically extract:

```bash
sudo mv /var/lib/postgresql/17/main /var/lib/postgresql/17/main.preserve.<ts>
sudo mkdir /var/lib/postgresql/17/main
sudo chown postgres:postgres /var/lib/postgresql/17/main
sudo chmod 700 /var/lib/postgresql/17/main
```

### 7.4 Run the restore

```bash
sudo -u postgres pgbackrest --stanza=nickerp \
  --type=time --target='2026-05-04 14:32:00+00' \
  --target-action=promote \
  restore
```

Flag-by-flag:
- `--type=time` — target shape; alternates: `lsn`, `name`,
  `xid`, `immediate`, `default`.
- `--target='2026-05-04 14:32:00+00'` — UTC offset is mandatory;
  pgbackrest does not assume the host's TZ.
- `--target-action=promote` — after recovery reaches the target,
  exit recovery mode and promote (the cluster is read-write again).
  Alternates: `pause` (default; cluster stays read-only until you
  explicitly `pg_wal_replay_resume()` or `promote`), `shutdown`.

### 7.5 Start Postgres + verify

```bash
sudo systemctl start postgresql

# Tail the log for the recovery-complete line:
sudo tail -f /var/log/postgresql/postgresql-17-main.log
```

Canonical "recovery succeeded" log lines:

```
LOG:  starting point-in-time recovery to 2026-05-04 14:32:00 UTC
LOG:  redo starts at 0/...
LOG:  consistent recovery state reached at 0/...
LOG:  recovery stopping before commit of transaction <xid>, time 2026-05-04 14:32:00.123 UTC
LOG:  selected new timeline ID: <n+1>
LOG:  archive recovery complete
LOG:  database system is ready to accept connections
```

The `selected new timeline ID` line means the cluster is now on a
new timeline — same divergence shape as a runbook 09 §7 failover.
Update [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md)'s §9.1
timeline check accordingly: if you have a standby, it cannot just
re-attach; re-baseline it.

### 7.6 Post-restore — verify data

Spot-check the restored data is at the expected point in time:

```bash
psql -U nscim_app -d nickerp_inspection \
  -c 'SELECT count(*) FROM inspection.cases WHERE "CreatedAt" > NOW() - INTERVAL '"'"'1 hour'"'"';'
# Expected: zero or near-zero rows IF the target was recent;
# or matches your pre-incident sample IF the target was historical.
```

Spot-check that the bad event's effect is gone — the dropped table
exists, the bad row insert is absent, the encrypted file is back to
its original bytes (per the application's storage expectations).

### 7.7 Stand the standby back up

After PITR, the primary is on a new timeline. The pre-existing
standby (if any) is on the old timeline and cannot stream. Apply
[`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) §8.1: rebuild
the standby from the new primary.

### 7.8 Trip back through §5.5 (verify the next backup runs cleanly)

A PITR-recovered cluster is a healthy cluster — pgbackrest works
against it normally. Run `pgbackrest --stanza=nickerp info`; the
backup history is preserved (the timeline change is reflected in
new WAL filenames, not in the backup metadata). The next scheduled
incremental in §6.1 will land cleanly.

## 8. Test-restore drill (quarterly)

Goal: prove the backups actually restore. Not a fire drill — a
calendar event. **Frequency: every quarter.** A drill that has not
run in two consecutive quarters is enough to declare the backups
"unverified" and re-prioritise this runbook.

### 8.1 Drill set-up

Pick a sandbox host that mirrors the prod box's PG version + OS
family. Linux VM is fine; the drill does not need the prod box's
network.

The sandbox must have:
- pgbackrest installed at the prod version.
- Read access to the repo (mount or rsync-down a copy of
  `/var/lib/pgbackrest`, or use S3 read-only credentials for the
  cloud variant).
- An empty `PGDATA` directory.

### 8.2 Pick a target

Pick a recent target in the PITR window — e.g. "yesterday at noon
UTC." Targets older than `repo1-retention-archive` (14 days
default) are not restorable; targets in the WAL-only zone (between
backups) require WAL replay over the whole window.

### 8.3 Run the restore

Same as §7.4, against the sandbox's `PGDATA`:

```bash
sudo -u postgres pgbackrest --stanza=nickerp \
  --type=time --target='<yesterday-noon> UTC' \
  --target-action=promote \
  restore
```

### 8.4 Sanity queries

After Postgres starts in §7.5, run sanity queries that prove the
restore reflects yesterday's state:

```bash
# Schema is intact:
psql -U postgres -d nickerp_inspection -c "\dt+ inspection.*" | head
psql -U postgres -d nickerp_platform   -c "\dt+ audit.*" | head

# Recent case-row count is plausible:
psql -U nscim_app -d nickerp_inspection -c \
  'SELECT count(*) FROM inspection.cases;'

# RLS policies are still installed:
psql -U postgres -d nickerp_inspection -c \
  "SELECT count(*) FROM pg_policies
   WHERE policyname LIKE 'tenant_isolation%';"
# Expected: 24 (post-Sprint-13; varies as new tables land).
```

Cross-check the row count against an out-of-band record (e.g. the
prod box's `pg_stat_user_tables` snapshot at the target time, or
the application's audit log).

### 8.5 Tear down + record

After the drill:

```bash
# Tear down the sandbox cluster.
sudo systemctl stop postgresql
sudo rm -rf /var/lib/postgresql/17/main/*
```

Append to `docs/runbooks/handoff-<date>-restore-drill.md` (one row
per drill):

```
## Restore drill: <YYYY-Q-N> - <YYYY-MM-DD>
- Sandbox host: <hostname>
- Target time: <UTC timestamp>
- Repo source: local | S3 | rsync
- Restore wall-clock: <minutes>
- Sanity queries: <pass / fail per query>
- Anomalies: <list>
- Followups: <list>
```

A drill that finds a problem is a successful drill. The point is to
catch problems at drill time, not at incident time.

## 9. Retention policy

The `[global]` config in §5.2 enforces retention automatically:

| Knob | Default | Effect |
|---|---|---|
| `repo1-retention-full=12` | 12 fulls | ~84 days of full coverage at weekly cadence |
| `repo1-retention-diff=4` | 4 diffs | belt-and-braces; v0 doesn't schedule diffs |
| `repo1-retention-archive=14` + `repo1-retention-archive-type=incr` | 14 days WAL after most-recent incremental | the **PITR window** |

When an older backup ages out, pgbackrest deletes it on the next
backup run (or `pgbackrest expire`). Disk reclaim is automatic.

Compliance alternates:
- Longer retention for finance / audit (e.g.
  `repo1-retention-full=52` for one year of weekly fulls). Costs
  ~12x repo disk.
- Cold-archive tier — copy aged-out fulls to a separate
  glacial-storage bucket via `pgbackrest archive-get` + a custom
  script. Out of scope for v0; tracked as a deferred follow-up.

Don't tune retention shorter than the PITR window: a
`retention-archive=7` plus a 14-day-old corruption means the bad
event is past the recoverable horizon.

## 10. Monitoring — recommended alerts

Wire these into Seq / your alerting layer. Each query is cheap.

### 10.1 Backup-lag alert

**Threshold:** no successful backup in 7 days. Fires P1.

**Query:**

```bash
pgbackrest --stanza=nickerp info --output=json | \
  jq -r '.[0].backup | max_by(.timestamp.stop) | .timestamp.stop'
# Compare to now() - 7 days.
```

A 7-day-no-backup trigger is the **R**ecovery **P**oint **O**bjective
floor. If the cron failed silently (e.g. WSL2 was off, or the
scheduled task didn't run), the alert catches it before the PITR
window erodes further.

### 10.2 Archive-failure alert

**Threshold:** `pg_stat_archiver.failed_count` > 0 in the last 1 h.
Fires P2.

**Query:**

```sql
SELECT failed_count, last_failed_wal, last_failed_time
FROM pg_stat_archiver
WHERE last_failed_time > now() - INTERVAL '1 hour';
```

Archiver failures mean WAL is piling up in `pg_wal/` and not
making it to the repo. Two consequences: (1) PITR is broken from
this point forward; (2) `pg_wal/` will eventually fill the disk.
Investigate the `archive_command` (often a permissions or
network-to-repo issue).

### 10.3 Repo-disk alert

**Threshold:** `df -h <repo-path>` reports < 20% free. Fires P2
(P1 if < 10%).

A repo that fills up cannot accept new backups; once it can't
accept WAL, archive-failure fires too. Provision more disk, prune
retention, or move to S3 (§5.6).

### 10.4 Backup-corruption alert

**Threshold:** `pgbackrest verify` reports any error. Fires P1.

**Run:** quarterly with the §8 drill, or weekly via cron:

```bash
0 6 * * 1 postgres pgbackrest --stanza=nickerp verify
```

`verify` re-reads every backup file in the repo, checks SHA1s
against the manifest, and flags drift / bit-rot. A repo that
fails verify is unreliable; restore from off-site (if any), or
re-base from the next §6 full.

## 11. Aftermath

### 11.1 Postmortem template (mandatory for restore + drill, optional for stand-up)

```
## pgbackrest <stand-up | restore | drill>: <YYYY-MM-DD HH:MM>
- Trigger: stand-up | data-loss-restore | corruption-restore | drill
- Stanza: <name>
- Repo: <local-path | s3://... | azure://...>
- Backup chain at start: <list of ids from `info` BEFORE>
- Target (if restore): <wall-clock | LSN | "n/a">
- Restore wall-clock: <minutes>
- Sanity queries: <pass / fail summary>
- New timeline ID (if restore was PITR): <n>
- Standby re-baselined? <yes / n/a>
- Anomalies: <list>
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 11.2 Who to notify

Single-engineer system today: capture the action in `CHANGELOG.md`
under a new dated bullet. A live restore is incident-grade; also
update any open issue and run the post-restore HA rebuild
([`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) §8.1) before
declaring done.

## 12. References

- `ROADMAP.md` §1 (locked answer 3) — pgbackrest as the v2 backup
  tool.
- [`09-postgres-ha-setup.md`](09-postgres-ha-setup.md) — the HA
  cluster pgbackrest backs up. §5.5 of runbook 09 forwards to §5
  here as a prerequisite.
- [`11-postgres-version-lock-pg17.md`](11-postgres-version-lock-pg17.md)
  — pgbackrest stanzas pin to a major version; PG upgrade requires
  a stanza re-stanza step.
- [`07-sprint-13-live-deploy.md`](07-sprint-13-live-deploy.md) §4.5
  — the precedent for "mandatory backup before destructive change."
  Once pgbackrest is in production, replace its `pg_dump` shape with
  a `pgbackrest backup` shape.
- [pgbackrest user guide](https://pgbackrest.org/user-guide.html)
  — upstream reference. Sections "Quick Start" (§5 here),
  "Restore" (§7 here), "Configuration Reference"
  (§5.2 here).
- [pgbackrest configuration reference](https://pgbackrest.org/configuration.html)
  — all `repo1-*` / `pg1-*` knobs.
- [PostgreSQL 17 docs — Continuous Archiving and Point-in-Time
  Recovery](https://www.postgresql.org/docs/17/continuous-archiving.html)
  — upstream reference for the WAL-archive primitives pgbackrest
  builds on.

