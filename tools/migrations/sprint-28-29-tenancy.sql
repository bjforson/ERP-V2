DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'tenancy') THEN
        CREATE SCHEMA tenancy;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS tenancy."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260426095043_Initial_AddTenancySchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'tenancy') THEN
            CREATE SCHEMA tenancy;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260426095043_Initial_AddTenancySchema') THEN
    CREATE TABLE tenancy.tenants (
        "Id" bigint GENERATED ALWAYS AS IDENTITY,
        "Code" character varying(64) NOT NULL,
        "Name" character varying(200) NOT NULL,
        "BillingPlan" character varying(50) NOT NULL DEFAULT 'internal',
        "TimeZone" character varying(64) NOT NULL DEFAULT 'Africa/Accra',
        "Locale" character varying(20) NOT NULL DEFAULT 'en-GH',
        "Currency" character varying(3) NOT NULL DEFAULT 'GHS',
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        CONSTRAINT "PK_tenants" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260426095043_Initial_AddTenancySchema') THEN
    INSERT INTO tenancy.tenants ("Id", "BillingPlan", "Code", "CreatedAt", "Currency", "IsActive", "Locale", "Name", "TimeZone")
    OVERRIDING SYSTEM VALUE
    VALUES (1, 'internal', 'nick-tc-scan', TIMESTAMPTZ '2026-04-26T00:00:00+00:00', 'GHS', TRUE, 'en-GH', 'Nick TC-Scan Operations', 'Africa/Accra');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260426095043_Initial_AddTenancySchema') THEN
    CREATE UNIQUE INDEX ux_tenants_code ON tenancy.tenants ("Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260426095043_Initial_AddTenancySchema') THEN
    PERFORM setval(
        pg_get_serial_sequence('tenancy.tenants', 'Id'),
        GREATEST(
            (SELECT MAX("Id") FROM tenancy.tenants) + 1,
            nextval(pg_get_serial_sequence('tenancy.tenants', 'Id'))),
        false);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260426095043_Initial_AddTenancySchema') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260426095043_Initial_AddTenancySchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211843_Add_RLS_Policies') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427211843_Add_RLS_Policies', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221311_Add_NscimAppRole_Grants') THEN

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
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221311_Add_NscimAppRole_Grants') THEN
    GRANT USAGE ON SCHEMA tenancy TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221311_Add_NscimAppRole_Grants') THEN
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA tenancy TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221311_Add_NscimAppRole_Grants') THEN
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA tenancy TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221311_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221311_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA tenancy GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221311_Add_NscimAppRole_Grants') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427221311_Add_NscimAppRole_Grants', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260428131013_Grant_NscimApp_CreateOnSchema') THEN
    GRANT CREATE ON SCHEMA tenancy TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260428131013_Grant_NscimApp_CreateOnSchema') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428131013_Grant_NscimApp_CreateOnSchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165546_Add_TenantVp6Settings') THEN
    ALTER TABLE tenancy.tenants ADD "AllowMultiServiceMembership" boolean NOT NULL DEFAULT TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165546_Add_TenantVp6Settings') THEN
    ALTER TABLE tenancy.tenants ADD "CaseVisibilityModel" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165546_Add_TenantVp6Settings') THEN
    UPDATE tenancy.tenants SET "AllowMultiServiceMembership" = TRUE
    WHERE "Id" = 1;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165546_Add_TenantVp6Settings') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260502165546_Add_TenantVp6Settings', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    ALTER TABLE tenancy.tenants ADD "State" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN

    UPDATE tenancy.tenants
    SET "State" = CASE WHEN "IsActive" = TRUE THEN 0 ELSE 10 END;

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    ALTER TABLE tenancy.tenants ADD "DeletedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    ALTER TABLE tenancy.tenants ADD "DeletedByUserId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    ALTER TABLE tenancy.tenants ADD "DeletionReason" character varying(500);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    ALTER TABLE tenancy.tenants ADD "RetentionDays" integer NOT NULL DEFAULT 90;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    ALTER TABLE tenancy.tenants ADD "HardPurgeAfter" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    ALTER TABLE tenancy.tenants DROP COLUMN "IsActive";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    CREATE INDEX ix_tenants_state ON tenancy.tenants ("State");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    CREATE TABLE tenancy.tenant_purge_log (
        "Id" uuid NOT NULL,
        "TenantId" bigint NOT NULL,
        "TenantCode" character varying(64) NOT NULL,
        "TenantName" character varying(200) NOT NULL,
        "PurgedAt" timestamp with time zone NOT NULL,
        "PurgedByUserId" uuid NOT NULL,
        "DeletionReason" character varying(500),
        "SoftDeletedAt" timestamp with time zone,
        "RowCounts" jsonb NOT NULL,
        "Outcome" character varying(32) NOT NULL,
        "FailureNote" character varying(1000),
        CONSTRAINT "PK_tenant_purge_log" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    CREATE INDEX ix_tenant_purge_log_purgedat ON tenancy.tenant_purge_log ("PurgedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN

    GRANT SELECT, INSERT ON tenancy.tenant_purge_log TO nscim_app;

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504103000_Add_TenantLifecyclePt1') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260504103000_Add_TenantLifecyclePt1', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504205232_Add_TenantExportRequests') THEN
    CREATE TABLE tenancy.tenant_export_requests (
        "Id" uuid NOT NULL,
        "TenantId" bigint NOT NULL,
        "RequestedAt" timestamp with time zone NOT NULL,
        "RequestedByUserId" uuid NOT NULL,
        "Format" integer NOT NULL DEFAULT 0,
        "Scope" integer NOT NULL DEFAULT 0,
        "Status" integer NOT NULL DEFAULT 0,
        "ArtifactPath" character varying(500),
        "ArtifactSizeBytes" bigint,
        "ArtifactSha256" bytea,
        "ExpiresAt" timestamp with time zone,
        "CompletedAt" timestamp with time zone,
        "FailureReason" character varying(1000),
        "DownloadCount" integer NOT NULL DEFAULT 0,
        "LastDownloadedAt" timestamp with time zone,
        "RevokedAt" timestamp with time zone,
        "RevokedByUserId" uuid,
        CONSTRAINT "PK_tenant_export_requests" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504205232_Add_TenantExportRequests') THEN
    CREATE INDEX ix_tenant_export_requests_status_requestedat ON tenancy.tenant_export_requests ("Status", "RequestedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504205232_Add_TenantExportRequests') THEN
    CREATE INDEX ix_tenant_export_requests_tenant_requestedat ON tenancy.tenant_export_requests ("TenantId", "RequestedAt" DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504205232_Add_TenantExportRequests') THEN

    GRANT SELECT, INSERT, UPDATE ON tenancy.tenant_export_requests TO nscim_app;

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504205232_Add_TenantExportRequests') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260504205232_Add_TenantExportRequests', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504221331_Add_TenantValidationRuleSettings') THEN
    CREATE TABLE tenancy.tenant_validation_rule_settings (
        "Id" uuid NOT NULL,
        "TenantId" bigint NOT NULL,
        "RuleId" character varying(128) NOT NULL,
        "Enabled" boolean NOT NULL DEFAULT TRUE,
        "UpdatedAt" timestamp with time zone NOT NULL,
        "UpdatedByUserId" uuid,
        CONSTRAINT "PK_tenant_validation_rule_settings" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504221331_Add_TenantValidationRuleSettings') THEN
    CREATE UNIQUE INDEX ux_tenant_validation_rule_settings_tenant_rule ON tenancy.tenant_validation_rule_settings ("TenantId", "RuleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504221331_Add_TenantValidationRuleSettings') THEN
    ALTER TABLE tenancy.tenant_validation_rule_settings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504221331_Add_TenantValidationRuleSettings') THEN
    ALTER TABLE tenancy.tenant_validation_rule_settings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504221331_Add_TenantValidationRuleSettings') THEN

    CREATE POLICY tenant_isolation_tenant_validation_rule_settings
      ON tenancy.tenant_validation_rule_settings
      USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint)
      WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504221331_Add_TenantValidationRuleSettings') THEN

    GRANT SELECT, INSERT, UPDATE ON tenancy.tenant_validation_rule_settings TO nscim_app;

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504221331_Add_TenantValidationRuleSettings') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260504221331_Add_TenantValidationRuleSettings', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504222014_Add_TenantModuleSettings') THEN
    CREATE TABLE tenancy.tenant_module_settings (
        "Id" bigint GENERATED ALWAYS AS IDENTITY,
        "TenantId" bigint NOT NULL,
        "ModuleId" character varying(64) NOT NULL,
        "Enabled" boolean NOT NULL DEFAULT TRUE,
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedByUserId" uuid,
        CONSTRAINT "PK_tenant_module_settings" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504222014_Add_TenantModuleSettings') THEN
    CREATE UNIQUE INDEX ux_tenant_module_settings_tenant_module ON tenancy.tenant_module_settings ("TenantId", "ModuleId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504222014_Add_TenantModuleSettings') THEN
    ALTER TABLE tenancy.tenant_module_settings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504222014_Add_TenantModuleSettings') THEN
    ALTER TABLE tenancy.tenant_module_settings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504222014_Add_TenantModuleSettings') THEN
    CREATE POLICY tenant_isolation_tenant_module_settings ON tenancy.tenant_module_settings USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM tenancy."__EFMigrationsHistory" WHERE "MigrationId" = '20260504222014_Add_TenantModuleSettings') THEN
    INSERT INTO tenancy."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260504222014_Add_TenantModuleSettings', '10.0.7');
    END IF;
END $EF$;
COMMIT;

