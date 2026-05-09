# HA provisioning checklist — Postgres primary + standby + pgbackrest

Sequencer for runbooks 09 (HA) + 10 (pgbackrest) at a fresh pilot site. Cross-refs point at canonical procedure. Do not deviate — this is a sequencer, not a substitute.

Posture assumed: Windows-host Postgres + SSH-Linux backup VM (runbook 10 §5A.2). Adjust §6.x commands if WSL2 v0 (§5A.3) or native v1 (§5A.4).

---

## Pre-flight

- [ ] Pilot site locked; `pilot-site-selection.md` go-no-go complete.
- [ ] Hardware spec reviewed by v2 dev team: 8c/32GB/1TB+256GB WAL on each node (runbook 14 §4.1).
- [ ] Both Postgres VMs on same LAN, sub-1ms RTT (rb14 §4.1).
- [ ] OS patched; OS family matches both nodes (no Linux↔Windows mix — rb09 §3 collation drift).
- [ ] Linux backup VM: 2 vCPU / 4GB / 100GB, same LAN, distinct hypervisor (rb14 §4.3).
- [ ] PG 17.x confirmed via `postgres -V` (rb11 lock).
- [ ] Outbound network from backup VM to off-site repo (S3/Azure) reachable (rb10 §5.6).
- [ ] Operator credentials issued: postgres OS user, deploy account, SSH keys.
- [ ] Change window approved; rollback plan written (rb14 §11.5).
- [ ] Backup-posture decision recorded: SSH-Linux / WSL2 v0 / native v1 (rb10 §5A.1).
- [ ] WAL volume mounted separately on each node (rb14 §4.1).

## Install

- [ ] Install PostgreSQL 17 on primary; `SELECT version();` reports 17.x (rb09 §4.1).
- [ ] Install PostgreSQL 17 on standby, same family + minor (rb09 §4.1).
- [ ] Primary `postgresql.conf`: `wal_level=replica`, `max_wal_senders=10`, `wal_keep_size=2GB`, `max_replication_slots=10`, `hot_standby=on`, `archive_mode=on`, `archive_command='pgbackrest --stanza=nickerp archive-push %p'` (rb09 §5.1).
- [ ] Primary `pg_hba.conf`: `host replication nickerp_repl <standby-ip>/32 scram-sha-256` (rb09 §5.2).
- [ ] Create `nickerp_repl` role (REPLICATION LOGIN, no DB grants) on primary (rb09 §5.3).
- [ ] Create physical replication slot `standby_<short-name>` on primary (rb09 §5.3).
- [ ] Capture `nickerp_repl` password in operator password manager (rb09 §5.3).
- [ ] Restart primary; verify `pg_is_in_recovery()`=`f` from standby host (rb09 §5.4).
- [ ] Install pgbackrest 2.50+ on backup VM (rb10 §5.1) and on primary for archive-push (§5A.2 step 6).
- [ ] Configure `/etc/pgbackrest/pgbackrest.conf` on backup VM: stanza `nickerp` + repo path (rb10 §5.2).
- [ ] Repository configured: NAS path or S3/Azure with cipher pass per SEC-SECRETS-5 (rb10 §5.6).
- [ ] SSH key wired from backup VM to primary `postgres` user (rb14 §5.4 + rb10 §5A.2).
- [ ] **Mandatory pre-standby backup**: `pgbackrest --stanza=nickerp --type=full backup` from backup VM (rb10 §5.4; rb09 §5.5 marks it hard prereq).
- [ ] `pg_basebackup` onto standby (PGDATA wiped first); writes `primary_conninfo` + `primary_slot_name` (rb09 §5.6).
- [ ] Verify `standby.signal` present + `primary_conninfo` references slot + nickerp_repl + passfile (rb09 §5.7).
- [ ] Start standby; tail log for `started streaming WAL from primary` (rb09 §5.8).

## Wire alerts

- [ ] Replication-lag: `apply_lag_time > 60s` on standby → P2 (rb09 §10.1).
- [ ] Standby-disconnected: `pg_stat_replication` 0 rows on primary → P2 (rb09 §10.2).
- [ ] Slot disk-fill: `confirmed_flush_lsn` stalled > 1h while WAL writes → P2 (P1 if disk < 80% free) (rb09 §10.3).
- [ ] Backup-lag: no successful backup in 7 days → P1 (rb10 §10.1).
- [ ] Archive-failure: `pg_stat_archiver.failed_count > 0` in last 1h → P2 (rb10 §10.2).
- [ ] Repo-disk: `< 20%` free → P2 (P1 if `< 10%`) (rb10 §10.3).
- [ ] Backup-corruption: `pgbackrest verify` non-zero → P1; weekly cron `0 6 * * 1 postgres pgbackrest --stanza=nickerp verify` (rb10 §10.4).
- [ ] Alert routing into Seq / ops alias: `MAILTO=` in cron or wrapper script (rb10 §6.2).
- [ ] Manual-failover drill alert: notify ops + business owner per rb09 §11.2.
- [ ] On-call escalation tested with synthetic alert before go-live.

## Smoke verification

- [ ] Replication streaming: `pg_stat_replication.state='streaming'` on primary; `pg_last_xact_replay_timestamp()` advancing on standby (rb09 §6.1 + §6.3).
- [ ] Standby identifies: `pg_is_in_recovery()`=`t` on standby (rb09 §6.2).
- [ ] App R/W routing: write succeeds on primary, read-only error on standby (rb09 §6.4).
- [ ] **Manual failover dry-run** on non-prod tenant: rb09 §7.1-§7.7; record wall-clock; fail back per §8.1.
- [ ] Full + incremental + PITR cycle: `pgbackrest check` then full → incremental → PITR-to-LSN restore on sandbox (rb10 §8 quarterly drill).

## Sign-off

- [ ] Operator signs pre-pilot HA stand-up: name + date + cluster fingerprint (timeline ID, slot name, repo path).
- [ ] On-call rotation updated; primary + standby + backup VM hostnames in rb09 §11.2 + rb10 §11.2 notify lists.
- [ ] Runbooks 09 + 10 + 14 bookmarked / linked from on-call dashboard.

All ticked → HA + backup posture ready for runbook 14 §10 (Phase V): security audit, perf load test, restore drill.
