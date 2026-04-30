DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'audit') THEN
        CREATE SCHEMA audit;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS audit."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'audit') THEN
            CREATE SCHEMA audit;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
    CREATE TABLE audit.events (
        "EventId" uuid NOT NULL DEFAULT (gen_random_uuid()),
        "TenantId" bigint NOT NULL,
        "ActorUserId" uuid,
        "CorrelationId" character varying(64),
        "EventType" character varying(200) NOT NULL,
        "EntityType" character varying(100) NOT NULL,
        "EntityId" character varying(200) NOT NULL,
        "Payload" jsonb NOT NULL,
        "OccurredAt" timestamp with time zone NOT NULL,
        "IngestedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "IdempotencyKey" character varying(128) NOT NULL,
        "PrevEventHash" character varying(64),
        CONSTRAINT "PK_events" PRIMARY KEY ("EventId")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
    CREATE INDEX ix_audit_events_actor_time ON audit.events ("TenantId", "ActorUserId", "OccurredAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
    CREATE INDEX ix_audit_events_correlation ON audit.events ("CorrelationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
    CREATE INDEX ix_audit_events_entity_time ON audit.events ("TenantId", "EntityType", "EntityId", "OccurredAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
    CREATE INDEX ix_audit_events_type_time ON audit.events ("TenantId", "EventType", "OccurredAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
    CREATE UNIQUE INDEX ux_audit_events_tenant_idempotency ON audit.events ("TenantId", "IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260426100152_Initial_AddAuditSchema') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260426100152_Initial_AddAuditSchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211851_Add_RLS_Policies') THEN
    ALTER TABLE audit.events ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211851_Add_RLS_Policies') THEN
    ALTER TABLE audit.events FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211851_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_events ON audit.events USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211851_Add_RLS_Policies') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427211851_Add_RLS_Policies', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221238_Add_NscimAppRole_Grants') THEN

    DO $$
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nscim_app') THEN
            CREATE ROLE nscim_app WITH LOGIN NOSUPERUSER NOBYPASSRLS;
        END IF;
    END $$;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221238_Add_NscimAppRole_Grants') THEN
    GRANT USAGE ON SCHEMA audit TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221238_Add_NscimAppRole_Grants') THEN
    GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA audit TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221238_Add_NscimAppRole_Grants') THEN
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA audit TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221238_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT ON TABLES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221238_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221238_Add_NscimAppRole_Grants') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427221238_Add_NscimAppRole_Grants', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260428131000_Grant_NscimApp_CreateOnSchema') THEN
    GRANT CREATE ON SCHEMA audit TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260428131000_Grant_NscimApp_CreateOnSchema') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428131000_Grant_NscimApp_CreateOnSchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260428140000_Grant_NscimApp_AuditHistoryWriteAccess') THEN
    GRANT UPDATE, DELETE ON audit."__EFMigrationsHistory" TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260428140000_Grant_NscimApp_AuditHistoryWriteAccess') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428140000_Grant_NscimApp_AuditHistoryWriteAccess', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260428194409_Make_TenantId_Nullable') THEN
    ALTER TABLE audit.events ALTER COLUMN "TenantId" DROP NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260428194409_Make_TenantId_Nullable') THEN
    CREATE INDEX ix_audit_events_system_type_time ON audit.events ("EventType", "OccurredAt") WHERE "TenantId" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260428194409_Make_TenantId_Nullable') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428194409_Make_TenantId_Nullable', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429061910_AddSystemContextOptInToEvents') THEN
    DROP POLICY IF EXISTS tenant_isolation_events ON audit.events;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429061910_AddSystemContextOptInToEvents') THEN
    CREATE POLICY tenant_isolation_events ON audit.events USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint OR (current_setting('app.tenant_id', true) = '-1' AND "TenantId" IS NULL)) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint OR (current_setting('app.tenant_id', true) = '-1' AND "TenantId" IS NULL));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429061910_AddSystemContextOptInToEvents') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429061910_AddSystemContextOptInToEvents', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429064002_Drop_PublicEFMigrationsHistory') THEN
    DROP TABLE IF EXISTS public."__EFMigrationsHistory";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429064002_Drop_PublicEFMigrationsHistory') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429064002_Drop_PublicEFMigrationsHistory', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    CREATE TABLE audit.notifications (
        "Id" uuid NOT NULL DEFAULT (gen_random_uuid()),
        "TenantId" bigint NOT NULL,
        "UserId" uuid NOT NULL,
        "EventId" uuid NOT NULL,
        "EventType" character varying(200) NOT NULL,
        "Title" character varying(200) NOT NULL,
        "Body" character varying(2000),
        "Link" character varying(500),
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ReadAt" timestamp with time zone,
        CONSTRAINT "PK_notifications" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_notifications_events_EventId" FOREIGN KEY ("EventId") REFERENCES audit.events ("EventId") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    CREATE TABLE audit.projection_checkpoints (
        "ProjectionName" character varying(100) NOT NULL,
        "LastIngestedAt" timestamp with time zone NOT NULL,
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        CONSTRAINT "PK_projection_checkpoints" PRIMARY KEY ("ProjectionName")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    CREATE INDEX "IX_notifications_EventId" ON audit.notifications ("EventId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    CREATE INDEX ix_notifications_user_unread ON audit.notifications ("UserId", "TenantId") WHERE "ReadAt" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    CREATE UNIQUE INDEX ux_notifications_user_event ON audit.notifications ("UserId", "EventId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    ALTER TABLE audit.notifications ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    ALTER TABLE audit.notifications FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    CREATE POLICY tenant_isolation_notifications ON audit.notifications USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    GRANT SELECT, INSERT, UPDATE ON audit.notifications TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    GRANT SELECT, INSERT, UPDATE ON audit.projection_checkpoints TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429073353_Add_Notifications_And_ProjectionCheckpoints') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429073353_Add_Notifications_And_ProjectionCheckpoints', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429114858_Promote_Notifications_UserIsolation_To_Rls') THEN
    DROP POLICY IF EXISTS tenant_isolation_notifications ON audit.notifications;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429114858_Promote_Notifications_UserIsolation_To_Rls') THEN
    CREATE POLICY tenant_user_isolation_notifications ON audit.notifications USING (("TenantId" = COALESCE(NULLIF(current_setting('app.tenant_id', true), ''), '0')::bigint AND "UserId" = COALESCE(NULLIF(current_setting('app.user_id', true), ''), '00000000-0000-0000-0000-000000000000')::uuid) OR (current_setting('app.tenant_id', true) = '-1')) WITH CHECK (("TenantId" = COALESCE(NULLIF(current_setting('app.tenant_id', true), ''), '0')::bigint AND "UserId" = COALESCE(NULLIF(current_setting('app.user_id', true), ''), '00000000-0000-0000-0000-000000000000')::uuid) OR (current_setting('app.tenant_id', true) = '-1'));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429114858_Promote_Notifications_UserIsolation_To_Rls') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429114858_Promote_Notifications_UserIsolation_To_Rls', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429141521_Add_EdgeNode_Authorizations_And_ReplayLog') THEN
    CREATE TABLE audit.edge_node_authorizations (
        "EdgeNodeId" character varying(100) NOT NULL,
        "TenantId" bigint NOT NULL,
        "AuthorizedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "AuthorizedByUserId" uuid,
        CONSTRAINT "PK_edge_node_authorizations" PRIMARY KEY ("EdgeNodeId", "TenantId")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429141521_Add_EdgeNode_Authorizations_And_ReplayLog') THEN
    CREATE TABLE audit.edge_node_replay_log (
        "Id" uuid NOT NULL DEFAULT (gen_random_uuid()),
        "EdgeNodeId" character varying(100) NOT NULL,
        "ReplayedAt" timestamp with time zone NOT NULL,
        "EventCount" integer NOT NULL,
        "OkCount" integer NOT NULL,
        "FailedCount" integer NOT NULL,
        "FailuresJson" jsonb,
        CONSTRAINT "PK_edge_node_replay_log" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429141521_Add_EdgeNode_Authorizations_And_ReplayLog') THEN
    CREATE INDEX ix_edge_node_replay_log_edge_time ON audit.edge_node_replay_log ("EdgeNodeId", "ReplayedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429141521_Add_EdgeNode_Authorizations_And_ReplayLog') THEN
    GRANT SELECT ON audit.edge_node_authorizations TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429141521_Add_EdgeNode_Authorizations_And_ReplayLog') THEN
    GRANT SELECT, INSERT ON audit.edge_node_replay_log TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260429141521_Add_EdgeNode_Authorizations_And_ReplayLog') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429141521_Add_EdgeNode_Authorizations_And_ReplayLog', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    CREATE TABLE audit.edge_node_api_keys (
        "Id" uuid NOT NULL DEFAULT (gen_random_uuid()),
        "TenantId" bigint NOT NULL,
        "EdgeNodeId" character varying(100) NOT NULL,
        "KeyHash" bytea NOT NULL,
        "KeyPrefix" character varying(8) NOT NULL,
        "IssuedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ExpiresAt" timestamp with time zone,
        "RevokedAt" timestamp with time zone,
        "Description" character varying(200),
        "CreatedByUserId" uuid,
        CONSTRAINT "PK_edge_node_api_keys" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    CREATE INDEX ix_edge_node_api_keys_tenant_edge ON audit.edge_node_api_keys ("TenantId", "EdgeNodeId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    CREATE UNIQUE INDEX ux_edge_node_api_keys_keyhash ON audit.edge_node_api_keys ("KeyHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    ALTER TABLE audit.edge_node_api_keys ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    ALTER TABLE audit.edge_node_api_keys FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    CREATE POLICY tenant_isolation_edge_node_api_keys ON audit.edge_node_api_keys USING (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint   OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1') WITH CHECK (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint   OR COALESCE(current_setting('app.tenant_id', true), '0') = '-1');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    GRANT SELECT, INSERT, UPDATE ON audit.edge_node_api_keys TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    REVOKE DELETE ON audit.edge_node_api_keys FROM nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM audit."__EFMigrationsHistory" WHERE "MigrationId" = '20260430105510_Add_EdgeNodeApiKeys') THEN
    INSERT INTO audit."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260430105510_Add_EdgeNodeApiKeys', '10.0.7');
    END IF;
END $EF$;
COMMIT;

