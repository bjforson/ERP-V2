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

