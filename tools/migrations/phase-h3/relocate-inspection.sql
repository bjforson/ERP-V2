-- Phase H3 — relocate __EFMigrationsHistory for the InspectionDbContext.
-- See ../phase-h3/relocate-migrations-history.sh for context.
--
-- Single context per DB here, so the row filter is simply "everything
-- in public's table" (matched by the inspection migrations on disk).
-- Idempotent: CREATE TABLE IF NOT EXISTS + INSERT … WHERE NOT EXISTS.

BEGIN;

-- 1. Mirror the structure of public.__EFMigrationsHistory inside the
--    inspection schema. INCLUDING ALL pulls primary key + defaults.
CREATE TABLE IF NOT EXISTS inspection."__EFMigrationsHistory"
    (LIKE public."__EFMigrationsHistory" INCLUDING ALL);

-- 2. Copy this context's rows from public if not already present.
--    The inspection DB only hosts the InspectionDbContext, so every
--    legitimate row in public belongs here. The dropped H3 attempt
--    left a "GrantMigrationsHistoryToNscimApp" row behind — exclude
--    it explicitly so the copy stays clean.
INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
SELECT "MigrationId", "ProductVersion"
FROM public."__EFMigrationsHistory"
WHERE "MigrationId" NOT LIKE '%_GrantMigrationsHistoryToNscimApp'
  AND "MigrationId" NOT IN (
      SELECT "MigrationId" FROM inspection."__EFMigrationsHistory"
  );

-- 3. Grant the per-table privileges nscim_app needs at runtime. EF
--    Core SELECTs and INSERTs against the history table; UPDATE +
--    DELETE happen only on rollback (Down direction), which we run
--    as postgres. Match the inspection schema's CRUD posture.
GRANT SELECT, INSERT, UPDATE, DELETE
    ON inspection."__EFMigrationsHistory"
    TO nscim_app;

-- 4. Revoke the posture-weakening grants the dropped H3 attempt
--    added on public. nscim_app no longer needs anything on public.
REVOKE SELECT, INSERT, UPDATE, DELETE
    ON public."__EFMigrationsHistory"
    FROM nscim_app;
REVOKE CREATE ON SCHEMA public FROM nscim_app;

-- 5. Drop the orphan migration rows from public so a future fresh
--    `dotnet ef database update --connection postgres` doesn't see
--    phantom history that no migration file matches.
DELETE FROM public."__EFMigrationsHistory"
WHERE "MigrationId" LIKE '%_GrantMigrationsHistoryToNscimApp';

COMMIT;
