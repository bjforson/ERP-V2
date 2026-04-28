#!/usr/bin/env bash
# Phase F5 — set the nscim_app role password from a deployment env var.
#
# The Add_NscimAppRole_Grants migrations create the role idempotently
# but never bake a password into git. This script runs the one-liner
# ALTER ROLE step out of band, reading the password from
# $NICKERP_APP_DB_PASSWORD (preferred) or $NICKSCAN_DB_PASSWORD (dev
# fallback). It must be run as a superuser (i.e. postgres).
#
# Usage:
#   export NICKSCAN_DB_PASSWORD="<the postgres superuser password>"
#   export NICKERP_APP_DB_PASSWORD="<password to set on nscim_app>"
#   # In dev you can leave NICKERP_APP_DB_PASSWORD unset; the script
#   # then reuses NICKSCAN_DB_PASSWORD for both, which is the documented
#   # dev convention.
#   ./tools/migrations/phase-f5/set-nscim-app-password.sh
#
# This script is idempotent — running it multiple times rotates the
# password to the latest env var value.

set -euo pipefail

PSQL=${PSQL:-"/c/Program Files/PostgreSQL/18/bin/psql.exe"}
PGHOST=${PGHOST:-localhost}
PGPORT=${PGPORT:-5432}

ADMIN_PASS=${NICKSCAN_DB_PASSWORD:?NICKSCAN_DB_PASSWORD must be set (the postgres superuser password)}
APP_PASS=${NICKERP_APP_DB_PASSWORD:-$ADMIN_PASS}

echo "Setting nscim_app password (length=${#APP_PASS}) on $PGHOST:$PGPORT ..."
# Pipe the SQL via stdin so psql does the variable interpolation
# (-c doesn't expand :'var', -v + stdin does).
PGPASSWORD="$ADMIN_PASS" "$PSQL" -h "$PGHOST" -p "$PGPORT" -U postgres -d postgres \
    -v "v_app_pass=$APP_PASS" \
    <<<"ALTER ROLE nscim_app WITH PASSWORD :'v_app_pass';"
echo "OK."
