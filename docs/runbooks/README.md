# NickERP v2 — Operations runbooks

This directory holds the canonical operational runbooks for ERP V2.
Each runbook follows the same eight-section structure (Symptom,
Severity, Quick triage, Diagnostic commands, Resolution, Verification,
Aftermath, References) and can be executed by an on-call engineer
without prior system context.

> **Sister docs.** [`../RUNBOOK.md`](../RUNBOOK.md) carries one-off
> migration cleanups (FU-6 etc.) — the kind of "here's the schema
> you might still bump into" notes that aren't an incident. This
> directory is for **named incidents** with a repeatable response.

---

## When-to-use decision tree

```
Are you responding to an alert / user report / log line?
├── No — you're shipping a planned change.
│   ├── Deploying a new build .................... 01-deploy.md
│   └── Rotating a secret ......................... 02-secret-rotation.md
│
└── Yes — what's the failure shape?
    ├── /healthz/ready is Unhealthy
    │   ├── postgres-* check fails ................ 02-secret-rotation.md §5
    │   │                                            (or 01-deploy.md if mid-deploy)
    │   ├── plugin-registry check fails ........... 04-plugin-load-failure.md
    │   ├── imaging-storage check fails ........... 03-prerender-stalled.md §5.4
    │   └── live but ready 503 with no obvious
    │       check failing ......................... 01-deploy.md (start over)
    │
    ├── Analyst UI shows previews stuck rendering . 03-prerender-stalled.md
    │
    ├── External-system pickup not happening
    │   (ICUMS specifically) ...................... 05-icums-outbox-backlog.md
    │
    ├── Auth started failing across the board
    │   (28P01 in logs) ........................... 02-secret-rotation.md
    │
    ├── Auth failing for one machine consumer ..... 02-secret-rotation.md §7
    │   (service-token rotation)
    │
    └── A capability silently disappeared
        (admin UI option missing, scanner
         no longer picks up files) ................ 04-plugin-load-failure.md
```

---

## Runbook index

| # | Title | Symptom | Severity ceiling |
|---|---|---|---|
| [01](01-deploy.md) | Deploying a new build to live | n/a — operator-initiated | P1 (rollback) |
| [02](02-secret-rotation.md) | Rotating secrets (DB password, CF Access AUD, service tokens) | n/a — operator-initiated; or 28P01 across the board | P1 (compromise) |
| [03](03-prerender-stalled.md) | `PreRenderWorker` not draining | analyst UI stuck on "rendering"; backlog growing | P1 (all tenants stuck) |
| [04](04-plugin-load-failure.md) | Plugin DLL fails to load on host start | `plugin-registry` Unhealthy, missing capability | P1 (zero plugins loaded) |
| [05](05-icums-outbox-backlog.md) | ICUMS file-based outbox backlog | files piling up unread in `OutboxPath` | P1 (>1000 files or > 4 h old) |

---

## Conventions

### Severity classes

- **P1** — production user-facing. Response inside 30 min – 1 h
  depending on scope. Wake someone up. Acceptable to take a host
  restart.
- **P2** — degraded but functional. Response inside 4 h. Don't wait
  for the next morning if the trend is bad.
- **P3** — cosmetic / single-row / single-tenant nuisance. Log,
  capture, fix in the next deploy.

Each runbook's §2 "Severity" table refines these per failure shape.

### Shell convention

All commands are written for **bash** (Git Bash on Windows, WSL,
or Linux). PowerShell equivalents are noted where they meaningfully
differ. Postgres commands use the v1 `psql` install path
(`/c/Program Files/PostgreSQL/18/bin/psql.exe`) when running as
`nscim_app`, and the system `psql` (resolved on PATH) when running
as `postgres`.

### Postgres connection

Two roles, two postures:

- **`postgres`** — superuser, `BYPASSRLS`. Used **only** for read-only
  introspection and emergency recovery. Never the host's runtime
  identity.
- **`nscim_app`** — the host's runtime identity. `LOGIN NOSUPERUSER
  NOBYPASSRLS`. Every diagnostic / resolution path that simulates
  what the host sees uses `nscim_app` and (where relevant) explicitly
  `SET app.tenant_id = '<tenant-id>'` to push the tenant the host's
  middleware would push.

The `app.tenant_id` mechanism is not optional. Skipping it under
`nscim_app` makes RLS hide every tenant-owned row, which is exactly
the regression Sprint 1 / H1 fixed at the host layer.

### Reading the host's logs

ERP V2 logs to **Seq** by default
(`http://localhost:5341`, configurable via `NickErp:Logging:SeqUrl`)
with a rolling-file fallback under
`C:\Shared\Logs\<service-name>-<date>.log`. Console output is also
on for interactive runs. Filter by `SourceContext` to scope to a
single subsystem; the canonical contexts referenced in the runbooks
are:

- `NickERP.Platform.Plugins.PluginLoader` — startup plugin discovery.
- `NickERP.Inspection.Imaging.PreRenderWorker` — image pipeline
  draining.
- `Startup.Migrations` — migration apply / skip.
- `NickERP.Platform.Identity.Auth.CfJwtValidator` — auth failures.

### "Restore minimal-privilege state"

Every runbook's Resolution section ends with a check that confirms
the system didn't end up with broader privileges than it started
with. This is non-negotiable: a deploy or recovery that requires
elevating to `postgres` is fine; a deploy or recovery that *leaves*
the host running as `postgres` is a regression. The check is
typically:

```bash
psql -U postgres -d postgres -c \
  "SELECT rolname, rolsuper, rolbypassrls
   FROM pg_roles WHERE rolname = 'nscim_app';"
# Expected: super=f, bypassrls=f.

psql -U postgres -d nickerp_inspection -c \
  "SELECT DISTINCT usename FROM pg_stat_activity
   WHERE datname = 'nickerp_inspection' AND state IS NOT NULL;"
# Expected: every row says nscim_app.
```

If you find yourself wanting to weaken this — **stop**. Read the
project's CLAUDE.md hard-rule §5: every weakening (AllowAnonymous,
loosened RLS, BYPASSRLS) needs explicit user confirmation, and you
must present at least one non-weakening alternative first.

---

## What's *not* yet in this tree

These runbooks are deferred until the system has the surface to
warrant them:

- **Edge node failover.** Phase 7.6, post-cutover.
- **Multi-host failover.** Today ERP V2 is single-host; multi-host
  posture lives in `ROADMAP.md` Phase 5+.
- **NickFinance / NickHR runbooks.** Out of scope until those modules
  ship in v2 (today they live only in v1, which has its own runbooks
  under `C:\Shared\NSCIM_PRODUCTION\docs\migration\RUNBOOK.md`).
- **Audit projection / notifications inbox.** Sprint 8 / P3 work.

When those arrive, add a runbook under the same eight-section
structure and link it from this README's decision tree.

---

## References

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) — system-of-record
  for the design every runbook assumes.
- [`../MIGRATIONS.md`](../MIGRATIONS.md) — applying schema changes
  under the `nscim_app` posture; the EF child-process env-var quirk.
- [`../RUNBOOK.md`](../RUNBOOK.md) — sister doc for one-off
  migration cleanups.
- [`../system-context-audit-register.md`](../system-context-audit-register.md)
  — the audit register for `ITenantContext.SetSystemContext()` opt-ins.
- [`../../PLAN.md`](../../PLAN.md) §18 — Sprint 7 / P1 origin.
- v1 reference — `C:\Shared\NSCIM_PRODUCTION\Deploy.ps1` and
  `C:\Shared\NSCIM_PRODUCTION\docs\migration\RUNBOOK.md`. Read-only;
  v1 is currently the live system. v2 ports patterns line-by-line,
  not by reference.
