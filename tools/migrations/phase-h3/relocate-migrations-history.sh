#!/usr/bin/env bash
# Phase H3 — relocate EF Core's __EFMigrationsHistory out of `public`
# into each DbContext's own schema (inspection / identity / audit /
# tenancy). Replaces the prior H3 attempt that broadened nscim_app's
# posture by granting CREATE on `public`.
#
# Why this exists:
#   The Phase F1 RLS rollout, Phase F5 non-superuser cutover, and the
#   current H3 multi-DB topology all want nscim_app to own as little of
#   `public` as possible. EF Core's history table is the only thing
#   keeping us tied to it — moving it into each context's own schema
#   (which nscim_app already has CRUD on by Phase F5) lets us drop the
#   one remaining grant the role still has on `public`.
#
# What this does:
#   1. For each DbContext / DB pair, creates
#      <schema>."__EFMigrationsHistory" (LIKE public's INCLUDING ALL).
#   2. Copies the rows belonging to that context across.
#   3. GRANTs SELECT/INSERT (audit) or full CRUD (others) on the new
#      table to nscim_app, matching each schema's existing posture.
#   4. REVOKEs the bad-commit grants from public — nscim_app loses
#      both CRUD on public."__EFMigrationsHistory" and CREATE on the
#      `public` schema. The old public history table is left in place
#      for safety (drop later once production has flipped).
#
# Run as postgres. Idempotent — re-runs are no-ops.
#
# Usage:
#   export NICKSCAN_DB_PASSWORD="<the postgres superuser password>"
#   ./tools/migrations/phase-h3/relocate-migrations-history.sh

set -euo pipefail

PSQL=${PSQL:-"/c/Program Files/PostgreSQL/18/bin/psql.exe"}
PGHOST=${PGHOST:-localhost}
PGPORT=${PGPORT:-5432}
PGUSER=${PGUSER:-postgres}

ADMIN_PASS=${NICKSCAN_DB_PASSWORD:?NICKSCAN_DB_PASSWORD must be set (the postgres superuser password)}

run_psql() {
    local db=$1
    shift
    PGPASSWORD="$ADMIN_PASS" "$PSQL" -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d "$db" -v ON_ERROR_STOP=1 "$@"
}

# Path of this script's dir, so the SQL files resolve regardless of
# where the caller cd'd.
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "[H3] Relocating __EFMigrationsHistory in nickerp_inspection ..."
run_psql nickerp_inspection -f "$script_dir/relocate-inspection.sql"

echo "[H3] Relocating __EFMigrationsHistory in nickerp_platform ..."
run_psql nickerp_platform -f "$script_dir/relocate-platform.sql"

echo "[H3] Done. nscim_app no longer needs CREATE on public."
echo "[H3] Verify with:"
echo "  SELECT has_schema_privilege('nscim_app', 'public', 'CREATE'); -- expect f"
