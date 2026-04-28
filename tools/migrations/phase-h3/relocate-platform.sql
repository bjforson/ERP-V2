-- Phase H3 — relocate __EFMigrationsHistory for nickerp_platform.
-- See ../phase-h3/relocate-migrations-history.sh for context.
--
-- Three DbContexts share this DB (Identity, Audit, Tenancy). We split
-- public's history rows across three new schema-scoped tables based on
-- the explicit MigrationId values committed to git for each context.
-- The lists below MUST be kept in sync with the Migrations/ folders.
-- Idempotent: every CREATE / INSERT is `IF NOT EXISTS` / `WHERE NOT EXISTS`.

BEGIN;

-- ---------------------------------------------------------------------------
-- Identity context
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS identity."__EFMigrationsHistory"
    (LIKE public."__EFMigrationsHistory" INCLUDING ALL);

INSERT INTO identity."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT "MigrationId", "ProductVersion"
FROM public."__EFMigrationsHistory"
WHERE "MigrationId" IN (
    '20260425205153_Initial_AddIdentitySchema',
    '20260427211743_Add_RLS_Policies',
    '20260427221152_Add_NscimAppRole_Grants',
    '20260428104421_RemoveRlsFromIdentityUsers'
)
AND "MigrationId" NOT IN (
    SELECT "MigrationId" FROM identity."__EFMigrationsHistory"
);

GRANT SELECT, INSERT, UPDATE, DELETE
    ON identity."__EFMigrationsHistory"
    TO nscim_app;

-- ---------------------------------------------------------------------------
-- Audit context — append-only posture; SELECT + INSERT only.
-- (UPDATE/DELETE only happen on rollback, which runs as postgres.)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS audit."__EFMigrationsHistory"
    (LIKE public."__EFMigrationsHistory" INCLUDING ALL);

INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT "MigrationId", "ProductVersion"
FROM public."__EFMigrationsHistory"
WHERE "MigrationId" IN (
    '20260426100152_Initial_AddAuditSchema',
    '20260427211851_Add_RLS_Policies',
    '20260427221238_Add_NscimAppRole_Grants'
)
AND "MigrationId" NOT IN (
    SELECT "MigrationId" FROM audit."__EFMigrationsHistory"
);

-- EF Core's Migrator takes `LOCK TABLE ... IN ACCESS EXCLUSIVE MODE` on
-- the history table at startup (Npgsql's NpgsqlHistoryRepository.AcquireDatabaseLock).
-- Postgres requires UPDATE/DELETE/TRUNCATE/MAINTAIN for that. Granting
-- full CRUD here is scoped to *just* this one history table — the
-- audit event tables themselves (audit_events) keep their append-only
-- posture (SELECT + INSERT only on `audit.audit_events`).
GRANT SELECT, INSERT, UPDATE, DELETE
    ON audit."__EFMigrationsHistory"
    TO nscim_app;

-- ---------------------------------------------------------------------------
-- Tenancy context
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS tenancy."__EFMigrationsHistory"
    (LIKE public."__EFMigrationsHistory" INCLUDING ALL);

INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT "MigrationId", "ProductVersion"
FROM public."__EFMigrationsHistory"
WHERE "MigrationId" IN (
    '20260426095043_Initial_AddTenancySchema',
    '20260427211843_Add_RLS_Policies',
    '20260427221311_Add_NscimAppRole_Grants'
)
AND "MigrationId" NOT IN (
    SELECT "MigrationId" FROM tenancy."__EFMigrationsHistory"
);

GRANT SELECT, INSERT, UPDATE, DELETE
    ON tenancy."__EFMigrationsHistory"
    TO nscim_app;

-- ---------------------------------------------------------------------------
-- Revoke the posture-weakening grants the dropped H3 attempt added.
-- ---------------------------------------------------------------------------
REVOKE SELECT, INSERT, UPDATE, DELETE
    ON public."__EFMigrationsHistory"
    FROM nscim_app;
REVOKE CREATE ON SCHEMA public FROM nscim_app;

-- ---------------------------------------------------------------------------
-- Drop the orphan rows the dropped H3 attempt left behind so the public
-- table stops looking like a partial mirror of the per-schema tables.
-- ---------------------------------------------------------------------------
DELETE FROM public."__EFMigrationsHistory"
WHERE "MigrationId" LIKE '%_GrantMigrationsHistoryToNscimApp';

COMMIT;
