-- ----------------------------------------------------------------------
-- NickERP v2 / Sprint 11 / P2 — edge node SQLite buffer schema.
--
-- This script seeds a fresh edge node's local SQLite buffer file. The
-- edge ships the .db file initialised from this script before the host
-- ever boots; runtime EnsureCreated() in Development is a convenience
-- only.
--
-- Manually transcribed from EF Core's `migrations script` output and
-- made idempotent (`IF NOT EXISTS`) — `dotnet ef migrations script
-- --idempotent` doesn't yet support SQLite (10.0.7). Re-generate with
-- `dotnet ef migrations script 0 <next-migration> -p apps/edge-node/...`
-- when adding follow-up migrations and adapt by hand.
--
-- Apply via:
--   sqlite3 edge-outbox.db < edge-outbox-schema.sql
-- ----------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

BEGIN TRANSACTION;

CREATE TABLE IF NOT EXISTS "edge_outbox" (
    "Id" INTEGER NOT NULL CONSTRAINT "PK_edge_outbox" PRIMARY KEY AUTOINCREMENT,
    "EventPayloadJson" TEXT NOT NULL,
    "EventTypeHint" TEXT NOT NULL,
    "EdgeTimestamp" TEXT NOT NULL,
    "EdgeNodeId" TEXT NOT NULL,
    "TenantId" INTEGER NOT NULL,
    "ReplayedAt" TEXT NULL,
    "ReplayAttempts" INTEGER NOT NULL DEFAULT 0,
    "LastReplayError" TEXT NULL
);

CREATE INDEX IF NOT EXISTS "ix_edge_outbox_pending"
    ON "edge_outbox" ("Id") WHERE "ReplayedAt" IS NULL;

INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260429141839_Init_EdgeOutbox', '10.0.7');

COMMIT;
