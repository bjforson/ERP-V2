# Runbook 02 — Rotating secrets

> **Scope.** Three classes of secret in ERP V2:
>
> 1. **`NICKSCAN_DB_PASSWORD`** — the `nscim_app` Postgres role password.
>    Used by every host that talks to `nickerp_platform` and
>    `nickerp_inspection`.
> 2. **ERP V2 auth signing key.** ERP V2 does **not** mint its own JWTs.
>    It validates Cloudflare Access JWTs against CF's JWKS — so the
>    rotatable artifact on our side is the **CF Access Application
>    Audience tag** (`NickErp:Identity:CfAccess:ApplicationAudience`)
>    plus, separately, any **service-token client secret hash** stored
>    in `identity.service_token_identities`.
> 3. **Per-plugin secrets.** Today the only plugin that holds anything
>    that resembles a secret is the ICUMS adapter's filesystem paths
>    (not credentials). When a plugin gains a real secret, follow
>    §6 here.
>
> **Sister docs:** [`01-deploy.md`](01-deploy.md) for the
> stop/start mechanics; [`docs/MIGRATIONS.md`](../MIGRATIONS.md) for
> the EF env-var quirk that bites password rotations specifically.

---

## 1. Symptom

You're rotating. Reasons:

- **Scheduled rotation.** Pick a cadence and stick to it (90-day
  default for DB passwords; CF Access tags are rotated when the CF
  application is reconfigured, not on a schedule).
- **Compromise suspected / confirmed.** Treat as P1. Do **not** wait
  for the next maintenance window.
- **Credential leaked into a log / commit / shell history.** Even if
  the leak was internal, rotate. Cost of paranoia is a host restart.

If a host is currently throwing
`28P01: password authentication failed for user "nscim_app"`, you're
not rotating — you're recovering. The mechanics below still apply,
but the order matters more (set new password → update env vars →
restart) and the smoke test is "host comes back up" not "host stays
up."

## 2. Severity

| Trigger | Severity | Response window |
|---|---|---|
| Scheduled rotation | n/a — operator-initiated | as scheduled |
| Suspected compromise | P1 | inside 1 h, host restart acceptable |
| Confirmed compromise | P1 | inside 15 min, do whatever it takes |
| Leaked-into-logs only | P2 | inside 24 h |

**Acceptable downtime during a rotation.** Restarting the host is
≤30 s; users see auth failures during the gap. Plan for it; do not
try to roll the password without restart — Postgres allows it but the
host's Npgsql connection pool will keep using the old password until
every pooled connection is recycled, and you'll spend longer chasing
half-failed requests than the restart would have cost.

## 3. Quick triage (60 seconds)

- **Is anyone else rotating?** Two operators rotating the same secret
  in parallel is the failure mode. Coordinate before starting.
- **Did the previous rotation leave breadcrumbs?** `git log --grep="rotate"`
  on this repo and on `docs/CHANGELOG.md` — confirms the last rotation
  date and the procedure that worked.
- **Is the rotation triggered by a real incident?** If yes, also start
  the postmortem template in §7 *now* — fields are easier to fill
  while the timeline is fresh.

## 4. Diagnostic commands

### 4.1 Confirm what's currently in use

```bash
# Postgres role state.
psql -U postgres -d postgres -c \
  "SELECT rolname, rolsuper, rolbypassrls,
          rolvaliduntil, rolconnlimit
   FROM pg_roles WHERE rolname = 'nscim_app';"

# Active connections — are any using nscim_app?
psql -U postgres -d nickerp_platform -c \
  "SELECT datname, usename, application_name, state, count(*)
   FROM pg_stat_activity
   WHERE usename = 'nscim_app'
   GROUP BY datname, usename, application_name, state;"
```

The second query tells you which hosts are connected. After the
rotation + restart, the same query should still show `nscim_app` rows
(the host reconnected with the new password).

### 4.2 Confirm the env-var the host reads

The connection string template lives in
`apps/portal/appsettings.json` and
`modules/inspection/src/NickERP.Inspection.Web/appsettings.json`:

```
"ConnectionStrings": {
  "Platform":   "Host=localhost;Port=5432;Database=nickerp_platform;Username=nscim_app;Password=__OVERRIDE_VIA_USER_SECRETS_OR_ENV__",
  "Inspection": "Host=localhost;Port=5432;Database=nickerp_inspection;Username=nscim_app;Password=__OVERRIDE_VIA_USER_SECRETS_OR_ENV__"
}
```

Hosts pick up the override from environment variables in the form
`ConnectionStrings__Platform=...;Password=$NICKSCAN_DB_PASSWORD` —
the `$NICKSCAN_DB_PASSWORD` is interpolated by **the shell that
launches the host**, not by the host itself.

```bash
# Check what the *current shell* would pass through.
env | grep -i NICKSCAN_DB_PASSWORD | sed 's/=.*/=[REDACTED]/'
```

If empty, the host is reading the literal `$NICKSCAN_DB_PASSWORD`
placeholder and authenticating with that as the password (which will
fail). Treat this as the rotation hint to also fix env-var hygiene.

### 4.3 Confirm CF Access audience

```bash
# Read the configured audience tag.
grep -r "ApplicationAudience" \
  modules/inspection/src/NickERP.Inspection.Web/appsettings.json \
  apps/portal/appsettings.json
```

In production, this should NOT be the literal
`__SET_IN_USER_SECRETS_OR_ENV__` placeholder. Either user-secrets
(dev) or env var
`NickErp__Identity__CfAccess__ApplicationAudience=...` (prod)
overrides it.

### 4.4 List service-token identities (machine credentials)

```bash
psql -U postgres -d nickerp_platform -c \
  'SELECT "Id", "TokenClientId", "DisplayName", "RevokedAt", "CreatedAt"
   FROM identity.service_token_identities
   ORDER BY "CreatedAt" DESC;'
```

A non-null `RevokedAt` means the token is already disabled. Active
tokens are the rotation candidates.

## 5. Resolution — `NICKSCAN_DB_PASSWORD`

This is the most-rotated secret. The other classes follow analogous
patterns; see §6 + §7.

### 5.1 Generate the new password

```bash
NEW_PASSWORD=$(openssl rand -base64 32 | tr -d '/+=' | head -c 32)
echo "Will set nscim_app to a 32-char password (not echoed)."
```

Use a password manager / vault to capture `$NEW_PASSWORD` *before*
applying. If the operator's terminal closes between §5.2 and §5.4,
the old password is gone and the new one isn't yet in the env var —
you'll have to lock-out-recover via `postgres`.

### 5.2 Apply at the DB

```bash
PGPASSWORD="${POSTGRES_SUPERUSER_PASSWORD}" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U postgres -d postgres -c \
  "ALTER ROLE nscim_app WITH PASSWORD '$NEW_PASSWORD';"
```

Verify by attempting a `SELECT 1` as `nscim_app` with the new
password:

```bash
PGPASSWORD="$NEW_PASSWORD" \
  '/c/Program Files/PostgreSQL/18/bin/psql.exe' \
  -U nscim_app -d nickerp_platform -c "SELECT 1;"
```

Returns `1` on success. On `28P01`, the `ALTER ROLE` didn't take —
back out and retry; do not proceed with §5.3.

### 5.3 Update the env-var the host reads

For an interactive shell-launched host (today's dev / staging
posture):

```bash
# Update the persistent user env-var on Windows.
setx NICKSCAN_DB_PASSWORD "$NEW_PASSWORD"
# `setx` updates the registry but does NOT update the current shell.
# Open a new shell to verify, OR `export` for the current session:
export NICKSCAN_DB_PASSWORD="$NEW_PASSWORD"
```

For a `nssm`-supervised service (when prod-deploy is wired):

```powershell
# v1 reference pattern — adapt for ERP V2 services as they're wired:
nssm set ERPV2_Inspection AppEnvironmentExtra `
  "+NICKSCAN_DB_PASSWORD=$NEW_PASSWORD"
```

`AppEnvironmentExtra +` appends without clobbering other env vars
already set for the service.

### 5.4 Restart the host

Per [`01-deploy.md`](01-deploy.md) §5.4. The host's startup log line
on a healthy reconnect is:

```
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (3ms) [...] SELECT 1
```

(visible at LogLevel `Information` for `Microsoft.EntityFrameworkCore.Database.Command`).
A bad password produces:

```
fail: Microsoft.EntityFrameworkCore.Database.Command[20102]
      Failed executing DbCommand [...]
      Npgsql.PostgresException: 28P01: password authentication failed
      for user "nscim_app"
```

…and `/healthz/ready` returns 503 with the
`postgres-platform-identity` (or `-audit`, `-inspection`) check
`Unhealthy`. Roll back §5.2 immediately if you see this — the only
authenticated path back is via `postgres`.

### 5.5 Update any sister hosts

Any other host with `nscim_app` in its connection string needs the
same env-var update + restart. As of Sprint 7 these are:

- ERP V2 Portal (`apps/portal`, `:5400`)
- ERP V2 Inspection.Web (`modules/inspection/src/NickERP.Inspection.Web`, `:5410`)
- (future modules per `ROADMAP.md`)

The `tools/migration-runner` console (v1 has it, v2 does not yet) and
any ad-hoc `dotnet ef` invocation also need the new password — but
that's a one-shot env update per shell, not a service restart.

### 5.6 Restore minimal-privilege state

After all hosts are up:

```bash
# Confirm nscim_app is still NOT a superuser.
psql -U postgres -d postgres -c \
  "SELECT rolname, rolsuper, rolbypassrls
   FROM pg_roles WHERE rolname = 'nscim_app';"
# Expected: super=f, bypassrls=f.

# Confirm hosts reconnected as nscim_app, not postgres.
psql -U postgres -d nickerp_inspection -c \
  "SELECT DISTINCT usename, application_name
   FROM pg_stat_activity
   WHERE datname = 'nickerp_inspection' AND state IS NOT NULL;"
# Expected: every row has usename = nscim_app.
```

If any host shows `usename = postgres`, that host has the dev-loop
shortcut connection string baked in (see
[`docs/MIGRATIONS.md`](../MIGRATIONS.md) §"Dev-cycle shortcut"). Fix
the connection string and restart again — RLS is silently disabled
under `postgres` because of `BYPASSRLS`, which is the exact regression
2026-04-25 fixed at the v1 layer (per
[`reference_rls_now_enforces.md`](../../README.md)). Don't ship that
on v2.

## 6. Resolution — CF Access Application Audience tag

ERP V2 doesn't mint its own JWTs; rotating "the auth signing key" on
our side means rotating the **audience tag** that pins the host to a
specific CF Access application.

### 6.1 Rotate at Cloudflare

In CF dashboard → Zero Trust → Access → Applications → ERP V2 host →
Settings → Application Audience (AUD) Tag → **Rotate**. Capture the
new value.

### 6.2 Update host config

```bash
# The new tag goes into config — environment variable preferred.
export NickErp__Identity__CfAccess__ApplicationAudience="$NEW_AUD_TAG"
```

For service-supervised hosts, the `nssm set ... AppEnvironmentExtra
"+NickErp__Identity__CfAccess__ApplicationAudience=..."` pattern from
§5.3 applies.

### 6.3 Restart and verify

Per §5.4. The auth handler log line on a successful first request is
nothing — the principal validates silently. A failure looks like:

```
warn: NickERP.Platform.Identity.Auth.CfJwtValidator[0]
      Failed to validate CF Access JWT: 'aud' claim mismatch
```

(emitted by `CfJwtValidator.ValidateAsync`, see
`platform/NickERP.Platform.Identity/Auth/CfJwtValidator.cs`).

A surprising number of failures here have nothing to do with the
audience and everything to do with `TeamDomain` being wrong (it's
the CF subdomain, e.g. `nickscan` — not a URL). Rotating audiences
without changing `TeamDomain` is the safe path.

## 7. Resolution — service-token client secrets

Service tokens (`identity.service_token_identities`) carry a hashed
client secret used for machine-to-machine auth. The hash is irreversible
— rotating means **issue a new token, hand it to the consumer, revoke
the old one**.

### 7.1 List active tokens

Per §4.4. Pick the `Id` of the token to rotate.

### 7.2 Issue replacement

The token issuance API is admin-only (auth-required). Today this
runs through the Inspection Web admin UI (Components/Pages
exposing the identity surface). For a CLI rotation, the SQL-direct
path is:

```sql
-- pseudo — confirm columns from your install:
INSERT INTO identity.service_token_identities (
  "Id", "TenantId", "TokenClientId", "TokenClientSecretHash",
  "DisplayName", "CreatedAt"
) VALUES (
  gen_random_uuid(), 1, '<new-client-id>', '<bcrypt-of-new-secret>',
  'Replacement for <old-id> rotation 2026-MM-DD', now()
);
```

Confirm against the actual entity definition in
`platform/NickERP.Platform.Identity/Entities/ServiceTokenIdentity.cs`
before running an INSERT. The hash function used by the host is the
one in `NickErpAuthenticationHandler` — match it exactly or auth will
silently fail.

### 7.3 Hand the new client-secret to the consumer

Out-of-band — vault, password manager, never via Slack / email
unencrypted.

### 7.4 Revoke the old token

```sql
UPDATE identity.service_token_identities
SET "RevokedAt" = now()
WHERE "Id" = '<old-id>';
```

Verify §4.4 shows `RevokedAt` populated. The host re-evaluates on
every request (no cache layer for revocation), so the next call from
the old client returns 401 — no host restart needed.

### 7.5 Aftermath check

```sql
SELECT "Id", "DisplayName", "CreatedAt", "RevokedAt"
FROM identity.service_token_identities
ORDER BY "CreatedAt" DESC LIMIT 5;
```

Expected: the new row exists with `RevokedAt = NULL`; the old row has
a populated `RevokedAt`.

## 8. Verification

For any rotation:

1. `/healthz/ready` returns `Healthy` (200) on the host that uses the
   rotated secret. **All five checks** must pass — partial green is
   not green.
2. A real authenticated request through the auth path succeeds:
   ```bash
   # In dev, with DevBypass enabled:
   curl -s -H "X-Dev-User: dev@nickscan.com" \
     http://127.0.0.1:5410/cases | head -c 200
   ```
   In prod, exercise the browser flow through CF Access.
3. Logs from the moment of rotation forward show no auth failures.
   Tail Seq filtered to the host name; `Warning` and `Error` events
   should be steady.
4. The §5.6 minimal-privilege check passes.

## 9. Aftermath

### 9.1 Postmortem template

```
## Rotation: <YYYY-MM-DD HH:MM> — <secret class>
- Trigger: scheduled | compromise-suspected | compromise-confirmed | leaked
- Host(s) restarted: <list>
- Total downtime (auth-failing window): <seconds>
- Old secret retired in: <password manager / vault path>
- Was nscim_app posture re-verified post-rotation? <yes / no>
- Any anomalies in /healthz/ready or the first 30 s of logs?
- Followups filed: <CHANGELOG.md / open-issue links>
```

### 9.2 Who to notify

Single-engineer system today: capture in `CHANGELOG.md` ("rotated
NICKSCAN_DB_PASSWORD on 2026-MM-DD") and update any ticket that
called for the rotation. If the rotation was incident-driven, also
update the relevant runbook.

## 10. References

- [`docs/MIGRATIONS.md`](../MIGRATIONS.md) — the EF child-process
  env-var quirk that mostly bites password rotations.
- [`docs/RUNBOOK.md`](../RUNBOOK.md) — sister runbook for one-off
  cleanups.
- [`docs/ARCHITECTURE.md`](../ARCHITECTURE.md) §7.1 — tenant + RLS
  posture; the "nscim_app must NOT be `BYPASSRLS`" invariant the
  rotation must preserve.
- v1 reference — `C:\Shared\NSCIM_PRODUCTION\Deploy.ps1` shows the
  `nssm set ... AppEnvironmentExtra` pattern that ports across.
- [`PLAN.md`](../../PLAN.md) §18 — Sprint 7 / P1 origin.
- [`platform/NickERP.Platform.Identity/IDENTITY.md`](../../platform/NickERP.Platform.Identity/IDENTITY.md)
  — service-token model, CF Access integration.
