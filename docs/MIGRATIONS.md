# NickERP v2 — applying EF migrations in dev and prod

> **Scope.** Operating EF Core migrations against the platform Postgres
> cluster — including the `nscim_app` (`LOGIN NOSUPERUSER NOBYPASSRLS`)
> production posture introduced in F5 slice 3 / H3.
> **Sister doc:** [`docs/MIGRATION-FROM-V1.md`](MIGRATION-FROM-V1.md) is
> about v1→v2 cutover, not schema migrations — different concern.

---

## Applying migrations on Windows under nscim_app

Anyone running `dotnet ef database update` from a Windows shell against the
`nscim_app` role will hit a child-process env-var quirk. This section
documents the symptom, the recommended workaround, and the dev-cycle
shortcut that sidesteps it.

### Symptom

`dotnet ef database update` returns:

```
Npgsql.PostgresException: 28P01: password authentication failed for user "nscim_app"
```

even though, in the **same shell**, this works:

```bash
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_platform -c "SELECT 1;"
```

The connection string EF reads (typically
`Host=localhost;...;Username=nscim_app;Password=$NICKSCAN_DB_PASSWORD`)
authenticates fine when the host process loads it at runtime — the
failure is specifically when `dotnet ef` spawns its child design-time
process.

### Hypothesis

`dotnet ef` spawns a child process (the design-time bundle) on Windows
that does not cleanly inherit `NICKSCAN_DB_PASSWORD` (or `PGPASSWORD`,
or whatever variable the connection string interpolates) from the
parent shell. Bash-on-Windows / Git Bash / `pwsh` all reproduce it. The
root cause lives inside `dotnet ef`'s process spawn; we don't fix it
here — we route around it.

Workarounds tested:

- **Inline the password into the connection string passed to `dotnet ef`.**
  Works, but fragile: leaks the secret into shell history and the
  process command line. Avoid.
- **Recommended: script + pipe to `psql`.** `dotnet ef migrations script
  ... --idempotent --output migration.sql` is purely local — no DB
  connection needed — so it doesn't trip the quirk. Then apply the
  generated SQL via `psql`, which reads `PGPASSWORD` from the
  environment correctly. The `--idempotent` flag means re-running
  against a partially-applied database is safe.

### Worked example

Apply the `AddSystemContextOptInToEvents` audit migration as
`nscim_app`:

```bash
cd "C:\Shared\ERP V2\platform\NickERP.Platform.Audit.Database"

# Generate an idempotent SQL script. <previous-id> is the last applied
# migration on the target DB; use "0" if applying from a fresh schema.
dotnet ef migrations script <previous-id> AddSystemContextOptInToEvents \
  --idempotent --output /tmp/migrate.sql --context AuditDbContext

# Apply via psql — reads PGPASSWORD correctly.
PGPASSWORD="$NICKSCAN_DB_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_platform -f /tmp/migrate.sql
```

Notes:

- Pass the `--context` flag whenever the project hosts more than one
  `DbContext` (e.g. `AuditDbContext` vs `IdentityDbContext` in
  `NickERP.Platform.Audit.Database`).
- `<previous-id>` is the last `MigrationId` already in the per-context
  `__EFMigrationsHistory` table. Look it up via
  `psql -c 'SELECT "MigrationId" FROM audit."__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;'`.
  Use `0` (or omit the from-arg, depending on EF version) when
  scripting the full history into a fresh DB.
- The script writes plain SQL — review it before applying to prod.
  `--idempotent` wraps each migration in a guard so partial state is
  safe to re-run.

### Dev-cycle shortcut: run migrations as `postgres`

In dev, you can avoid the quirk entirely by pointing the migration's
connection string at the `postgres` superuser instead of `nscim_app`:

```
ConnectionStrings__Audit=Host=localhost;Port=5432;Database=nickerp_platform;Username=postgres;Password=$NICKSCAN_DB_PASSWORD
```

`dotnet ef database update` then succeeds because the password ends up
in the connection string EF reads (no env-var interpolation hop) and
because `postgres` authenticates against any role.

**This is the recommended dev-loop workflow.** The host itself still
runs as `nscim_app` (so RLS actually enforces — `postgres` has
`BYPASSRLS` and silently nullifies every policy). Migrations are a
separate connection; running them as `postgres` is consistent with
real prod posture too, where DDL is run by an operator with elevated
rights, not by the application.

The script-and-pipe pattern above is the **production-like** path —
use it when you're rehearsing a prod cutover, validating that
`nscim_app`'s grants are sufficient for the migration in question, or
running on a host where the operator only has the `nscim_app`
credentials.

### Why we don't fix the root cause

The quirk lives in `dotnet ef`'s shell-to-child env-var passthrough on
Windows — outside our codebase, low ROI to chase. The script-and-pipe
workaround is documented, two lines, and matches how prod cutovers
should run anyway (a reviewable SQL artifact, not a black-box
`Database.Migrate()` call).

If a future .NET / EF Core release fixes this, drop this section.
Until then: assume the quirk, run migrations the documented way.
