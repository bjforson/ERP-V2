DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'identity') THEN
        CREATE SCHEMA identity;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS identity."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'identity') THEN
            CREATE SCHEMA identity;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE TABLE identity.app_scopes (
        "Id" uuid NOT NULL,
        "Code" character varying(100) NOT NULL,
        "AppName" character varying(50) NOT NULL,
        "Description" character varying(500),
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_app_scopes" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE TABLE identity.identity_users (
        "Id" uuid NOT NULL,
        "Email" character varying(320) NOT NULL,
        "NormalizedEmail" character varying(320) NOT NULL,
        "DisplayName" character varying(200),
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "LastSeenAt" timestamp with time zone,
        "TenantId" bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_identity_users" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE TABLE identity.service_token_identities (
        "Id" uuid NOT NULL,
        "TokenClientId" character varying(255) NOT NULL,
        "DisplayName" character varying(200) NOT NULL,
        "Purpose" character varying(500),
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "LastSeenAt" timestamp with time zone,
        "ExpiresAt" timestamp with time zone,
        "TenantId" bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_service_token_identities" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE TABLE identity.user_scopes (
        "Id" uuid NOT NULL,
        "IdentityUserId" uuid NOT NULL,
        "AppScopeCode" character varying(100) NOT NULL,
        "GrantedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "GrantedByUserId" uuid NOT NULL,
        "ExpiresAt" timestamp with time zone,
        "RevokedAt" timestamp with time zone,
        "RevokedByUserId" uuid,
        "Notes" character varying(500),
        "TenantId" bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_user_scopes" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_user_scopes_identity_users_IdentityUserId" FOREIGN KEY ("IdentityUserId") REFERENCES identity.identity_users ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE TABLE identity.service_token_scopes (
        "Id" uuid NOT NULL,
        "ServiceTokenIdentityId" uuid NOT NULL,
        "AppScopeCode" character varying(100) NOT NULL,
        "GrantedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "GrantedByUserId" uuid NOT NULL,
        "ExpiresAt" timestamp with time zone,
        "RevokedAt" timestamp with time zone,
        "RevokedByUserId" uuid,
        "TenantId" bigint NOT NULL DEFAULT 1,
        CONSTRAINT "PK_service_token_scopes" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_service_token_scopes_service_token_identities_ServiceTokenI~" FOREIGN KEY ("ServiceTokenIdentityId") REFERENCES identity.service_token_identities ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_app_scopes_tenant_app ON identity.app_scopes ("TenantId", "AppName");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE UNIQUE INDEX ux_app_scopes_tenant_code ON identity.app_scopes ("TenantId", "Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_identity_users_last_seen ON identity.identity_users ("LastSeenAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_identity_users_tenant ON identity.identity_users ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE UNIQUE INDEX ux_identity_users_tenant_normalized_email ON identity.identity_users ("TenantId", "NormalizedEmail");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_service_tokens_tenant ON identity.service_token_identities ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE UNIQUE INDEX ux_service_tokens_tenant_client_id ON identity.service_token_identities ("TenantId", "TokenClientId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_service_token_scopes_revoked_at ON identity.service_token_scopes ("RevokedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX "IX_service_token_scopes_ServiceTokenIdentityId" ON identity.service_token_scopes ("ServiceTokenIdentityId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_service_token_scopes_tenant_token_scope ON identity.service_token_scopes ("TenantId", "ServiceTokenIdentityId", "AppScopeCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX "IX_user_scopes_IdentityUserId" ON identity.user_scopes ("IdentityUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_user_scopes_revoked_at ON identity.user_scopes ("RevokedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_user_scopes_tenant_scope ON identity.user_scopes ("TenantId", "AppScopeCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    CREATE INDEX ix_user_scopes_tenant_user_scope ON identity.user_scopes ("TenantId", "IdentityUserId", "AppScopeCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260425205153_Initial_AddIdentitySchema') THEN
    INSERT INTO identity."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260425205153_Initial_AddIdentitySchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.identity_users ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.identity_users FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_identity_users ON identity.identity_users USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.app_scopes ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.app_scopes FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_app_scopes ON identity.app_scopes USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.user_scopes ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.user_scopes FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_user_scopes ON identity.user_scopes USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.service_token_identities ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.service_token_identities FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_service_token_identities ON identity.service_token_identities USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.service_token_scopes ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    ALTER TABLE identity.service_token_scopes FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_service_token_scopes ON identity.service_token_scopes USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211743_Add_RLS_Policies') THEN
    INSERT INTO identity."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427211743_Add_RLS_Policies', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221152_Add_NscimAppRole_Grants') THEN

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
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221152_Add_NscimAppRole_Grants') THEN
    GRANT USAGE ON SCHEMA identity TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221152_Add_NscimAppRole_Grants') THEN
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221152_Add_NscimAppRole_Grants') THEN
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221152_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA identity GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221152_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA identity GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221152_Add_NscimAppRole_Grants') THEN
    INSERT INTO identity."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427221152_Add_NscimAppRole_Grants', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104421_RemoveRlsFromIdentityUsers') THEN
    DROP POLICY IF EXISTS tenant_isolation_identity_users ON identity.identity_users;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104421_RemoveRlsFromIdentityUsers') THEN
    ALTER TABLE identity.identity_users NO FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104421_RemoveRlsFromIdentityUsers') THEN
    ALTER TABLE identity.identity_users DISABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104421_RemoveRlsFromIdentityUsers') THEN
    INSERT INTO identity."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428104421_RemoveRlsFromIdentityUsers', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260428130946_Grant_NscimApp_CreateOnSchema') THEN
    GRANT CREATE ON SCHEMA identity TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM identity."__EFMigrationsHistory" WHERE "MigrationId" = '20260428130946_Grant_NscimApp_CreateOnSchema') THEN
    INSERT INTO identity."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428130946_Grant_NscimApp_CreateOnSchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

