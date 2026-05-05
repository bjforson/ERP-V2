DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'inspection') THEN
        CREATE SCHEMA inspection;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS inspection."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'inspection') THEN
            CREATE SCHEMA inspection;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE TABLE inspection.external_system_instances (
        "Id" uuid NOT NULL,
        "TypeCode" character varying(64) NOT NULL,
        "DisplayName" character varying(200) NOT NULL,
        "Description" character varying(500),
        "Scope" integer NOT NULL,
        "ConfigJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_external_system_instances" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE TABLE inspection.locations (
        "Id" uuid NOT NULL,
        "Code" character varying(64) NOT NULL,
        "Name" character varying(200) NOT NULL,
        "Region" character varying(100),
        "TimeZone" character varying(64) NOT NULL,
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_locations" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE TABLE inspection.external_system_bindings (
        "Id" uuid NOT NULL,
        "ExternalSystemInstanceId" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "Role" character varying(32) NOT NULL DEFAULT 'primary',
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_external_system_bindings" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_external_system_bindings_external_system_instances_External~" FOREIGN KEY ("ExternalSystemInstanceId") REFERENCES inspection.external_system_instances ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_external_system_bindings_locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES inspection.locations ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE TABLE inspection.stations (
        "Id" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "Code" character varying(64) NOT NULL,
        "Name" character varying(200) NOT NULL,
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_stations" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_stations_locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES inspection.locations ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE TABLE inspection.scanner_device_instances (
        "Id" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "StationId" uuid,
        "TypeCode" character varying(64) NOT NULL,
        "DisplayName" character varying(200) NOT NULL,
        "Description" character varying(500),
        "ConfigJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_scanner_device_instances" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_scanner_device_instances_locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES inspection.locations ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_scanner_device_instances_stations_StationId" FOREIGN KEY ("StationId") REFERENCES inspection.stations ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX "IX_external_system_bindings_ExternalSystemInstanceId" ON inspection.external_system_bindings ("ExternalSystemInstanceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX "IX_external_system_bindings_LocationId" ON inspection.external_system_bindings ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE UNIQUE INDEX ux_external_bindings_tenant_inst_loc ON inspection.external_system_bindings ("TenantId", "ExternalSystemInstanceId", "LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX ix_external_systems_tenant ON inspection.external_system_instances ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX ix_external_systems_type ON inspection.external_system_instances ("TypeCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX ix_locations_tenant ON inspection.locations ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE UNIQUE INDEX ux_locations_tenant_code ON inspection.locations ("TenantId", "Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX "IX_scanner_device_instances_LocationId" ON inspection.scanner_device_instances ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX "IX_scanner_device_instances_StationId" ON inspection.scanner_device_instances ("StationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX ix_scanners_tenant ON inspection.scanner_device_instances ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX ix_scanners_tenant_loc ON inspection.scanner_device_instances ("TenantId", "LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX ix_scanners_type ON inspection.scanner_device_instances ("TypeCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX "IX_stations_LocationId" ON inspection.stations ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE INDEX ix_stations_tenant ON inspection.stations ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    CREATE UNIQUE INDEX ux_stations_tenant_loc_code ON inspection.stations ("TenantId", "LocationId", "Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426105303_Initial_AddInspectionSchema') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260426105303_Initial_AddInspectionSchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.cases (
        "Id" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "StationId" uuid,
        "SubjectType" integer NOT NULL,
        "SubjectIdentifier" character varying(200) NOT NULL,
        "SubjectPayloadJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "State" integer NOT NULL,
        "OpenedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "StateEnteredAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ClosedAt" timestamp with time zone,
        "OpenedByUserId" uuid,
        "AssignedAnalystUserId" uuid,
        "CorrelationId" character varying(64),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_cases" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_cases_locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES inspection.locations ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_cases_stations_StationId" FOREIGN KEY ("StationId") REFERENCES inspection.stations ("Id") ON DELETE SET NULL
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.location_assignments (
        "Id" uuid NOT NULL,
        "IdentityUserId" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "Roles" character varying(500) NOT NULL DEFAULT '',
        "GrantedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "GrantedByUserId" uuid NOT NULL,
        "ExpiresAt" timestamp with time zone,
        "IsActive" boolean NOT NULL DEFAULT TRUE,
        "Notes" character varying(500),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_location_assignments" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_location_assignments_locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES inspection.locations ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.authority_documents (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "ExternalSystemInstanceId" uuid NOT NULL,
        "DocumentType" character varying(64) NOT NULL,
        "ReferenceNumber" character varying(200) NOT NULL,
        "PayloadJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "ReceivedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_authority_documents" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_authority_documents_cases_CaseId" FOREIGN KEY ("CaseId") REFERENCES inspection.cases ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_authority_documents_external_system_instances_ExternalSyste~" FOREIGN KEY ("ExternalSystemInstanceId") REFERENCES inspection.external_system_instances ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.outbound_submissions (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "ExternalSystemInstanceId" uuid NOT NULL,
        "PayloadJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "IdempotencyKey" character varying(128) NOT NULL,
        "Status" character varying(32) NOT NULL DEFAULT 'pending',
        "ResponseJson" jsonb,
        "ErrorMessage" character varying(2000),
        "SubmittedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "RespondedAt" timestamp with time zone,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_outbound_submissions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_outbound_submissions_cases_CaseId" FOREIGN KEY ("CaseId") REFERENCES inspection.cases ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_outbound_submissions_external_system_instances_ExternalSyst~" FOREIGN KEY ("ExternalSystemInstanceId") REFERENCES inspection.external_system_instances ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.review_sessions (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "AnalystUserId" uuid NOT NULL,
        "StartedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "EndedAt" timestamp with time zone,
        "Outcome" character varying(32) NOT NULL DEFAULT 'in-progress',
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_review_sessions" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_review_sessions_cases_CaseId" FOREIGN KEY ("CaseId") REFERENCES inspection.cases ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.scans (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "ScannerDeviceInstanceId" uuid NOT NULL,
        "Mode" character varying(64) NOT NULL,
        "CapturedAt" timestamp with time zone NOT NULL,
        "OperatorUserId" uuid,
        "IdempotencyKey" character varying(128) NOT NULL,
        "CorrelationId" character varying(64),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_scans" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_scans_cases_CaseId" FOREIGN KEY ("CaseId") REFERENCES inspection.cases ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_scans_scanner_device_instances_ScannerDeviceInstanceId" FOREIGN KEY ("ScannerDeviceInstanceId") REFERENCES inspection.scanner_device_instances ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.verdicts (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "Decision" integer NOT NULL,
        "Basis" character varying(2000) NOT NULL,
        "DecidedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "DecidedByUserId" uuid NOT NULL,
        "RevisedVerdictId" uuid,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_verdicts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_verdicts_cases_CaseId" FOREIGN KEY ("CaseId") REFERENCES inspection.cases ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.analyst_reviews (
        "Id" uuid NOT NULL,
        "ReviewSessionId" uuid NOT NULL,
        "TimeToDecisionMs" integer NOT NULL,
        "RoiInteractionsJson" jsonb NOT NULL DEFAULT ('[]'::jsonb),
        "ConfidenceScore" double precision NOT NULL,
        "VerdictChangesJson" jsonb NOT NULL DEFAULT ('[]'::jsonb),
        "PeerDisagreementCount" integer NOT NULL,
        "PostHocOutcomeJson" jsonb,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_analyst_reviews" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_analyst_reviews_review_sessions_ReviewSessionId" FOREIGN KEY ("ReviewSessionId") REFERENCES inspection.review_sessions ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.scan_artifacts (
        "Id" uuid NOT NULL,
        "ScanId" uuid NOT NULL,
        "ArtifactKind" character varying(32) NOT NULL DEFAULT 'Primary',
        "StorageUri" character varying(500) NOT NULL,
        "MimeType" character varying(64) NOT NULL,
        "WidthPx" integer NOT NULL,
        "HeightPx" integer NOT NULL,
        "Channels" integer NOT NULL,
        "ContentHash" character varying(128) NOT NULL,
        "MetadataJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_scan_artifacts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_scan_artifacts_scans_ScanId" FOREIGN KEY ("ScanId") REFERENCES inspection.scans ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE TABLE inspection.findings (
        "Id" uuid NOT NULL,
        "AnalystReviewId" uuid NOT NULL,
        "FindingType" character varying(64) NOT NULL,
        "Severity" character varying(16) NOT NULL DEFAULT 'info',
        "LocationInImageJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "Note" character varying(2000),
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_findings" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_findings_analyst_reviews_AnalystReviewId" FOREIGN KEY ("AnalystReviewId") REFERENCES inspection.analyst_reviews ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_analyst_reviews_ReviewSessionId" ON inspection.analyst_reviews ("ReviewSessionId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_authority_docs_tenant_case ON inspection.authority_documents ("TenantId", "CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_authority_docs_tenant_ref ON inspection.authority_documents ("TenantId", "ReferenceNumber");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_authority_documents_CaseId" ON inspection.authority_documents ("CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_authority_documents_ExternalSystemInstanceId" ON inspection.authority_documents ("ExternalSystemInstanceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_cases_assigned_analyst ON inspection.cases ("AssignedAnalystUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_cases_LocationId" ON inspection.cases ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_cases_StationId" ON inspection.cases ("StationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_cases_tenant_loc_state_time ON inspection.cases ("TenantId", "LocationId", "State", "OpenedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_cases_tenant_subject ON inspection.cases ("TenantId", "SubjectIdentifier");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_findings_AnalystReviewId" ON inspection.findings ("AnalystReviewId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_findings_severity ON inspection.findings ("Severity");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_findings_tenant_type ON inspection.findings ("TenantId", "FindingType");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_location_assignments_loc ON inspection.location_assignments ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_location_assignments_user ON inspection.location_assignments ("IdentityUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE UNIQUE INDEX ux_location_assignments_tenant_user_loc ON inspection.location_assignments ("TenantId", "IdentityUserId", "LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_outbound_submissions_CaseId" ON inspection.outbound_submissions ("CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_outbound_submissions_ExternalSystemInstanceId" ON inspection.outbound_submissions ("ExternalSystemInstanceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_outbound_tenant_status ON inspection.outbound_submissions ("TenantId", "Status");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE UNIQUE INDEX ux_outbound_tenant_idempotency ON inspection.outbound_submissions ("TenantId", "IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_review_sessions_analyst ON inspection.review_sessions ("AnalystUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_review_sessions_CaseId" ON inspection.review_sessions ("CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_review_sessions_tenant_case_time ON inspection.review_sessions ("TenantId", "CaseId", "StartedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_scan_artifacts_content_hash ON inspection.scan_artifacts ("ContentHash");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_scan_artifacts_ScanId" ON inspection.scan_artifacts ("ScanId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_scan_artifacts_tenant_scan ON inspection.scan_artifacts ("TenantId", "ScanId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX "IX_scans_CaseId" ON inspection.scans ("CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_scans_device ON inspection.scans ("ScannerDeviceInstanceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_scans_tenant_case_time ON inspection.scans ("TenantId", "CaseId", "CapturedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE UNIQUE INDEX ux_scans_tenant_idempotency ON inspection.scans ("TenantId", "IdempotencyKey");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE INDEX ix_verdicts_tenant_decision_time ON inspection.verdicts ("TenantId", "Decision", "DecidedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    CREATE UNIQUE INDEX ux_verdicts_case ON inspection.verdicts ("CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260426171815_Add_CaseLifecycle_And_LocationAssignments') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260426171815_Add_CaseLifecycle_And_LocationAssignments', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427164855_Add_ScanRenderArtifact') THEN
    CREATE TABLE inspection.scan_render_artifacts (
        "Id" uuid NOT NULL,
        "ScanArtifactId" uuid NOT NULL,
        "Kind" character varying(32) NOT NULL,
        "StorageUri" character varying(500) NOT NULL,
        "WidthPx" integer NOT NULL,
        "HeightPx" integer NOT NULL,
        "MimeType" character varying(64) NOT NULL,
        "ContentHash" character varying(128) NOT NULL,
        "RenderedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_scan_render_artifacts" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_scan_render_artifacts_scan_artifacts_ScanArtifactId" FOREIGN KEY ("ScanArtifactId") REFERENCES inspection.scan_artifacts ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427164855_Add_ScanRenderArtifact') THEN
    CREATE INDEX ix_render_tenant_artifact ON inspection.scan_render_artifacts ("TenantId", "ScanArtifactId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427164855_Add_ScanRenderArtifact') THEN
    CREATE UNIQUE INDEX ux_render_artifact_kind ON inspection.scan_render_artifacts ("ScanArtifactId", "Kind");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427164855_Add_ScanRenderArtifact') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427164855_Add_ScanRenderArtifact', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.locations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.locations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_locations ON inspection.locations USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.stations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.stations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_stations ON inspection.stations USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scanner_device_instances ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scanner_device_instances FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_scanner_device_instances ON inspection.scanner_device_instances USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.external_system_instances ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.external_system_instances FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_external_system_instances ON inspection.external_system_instances USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.external_system_bindings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.external_system_bindings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_external_system_bindings ON inspection.external_system_bindings USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.location_assignments ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.location_assignments FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_location_assignments ON inspection.location_assignments USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.cases ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.cases FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_cases ON inspection.cases USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scans ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scans FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_scans ON inspection.scans USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scan_artifacts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scan_artifacts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_scan_artifacts ON inspection.scan_artifacts USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scan_render_artifacts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.scan_render_artifacts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_scan_render_artifacts ON inspection.scan_render_artifacts USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.authority_documents ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.authority_documents FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_authority_documents ON inspection.authority_documents USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.review_sessions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.review_sessions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_review_sessions ON inspection.review_sessions USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.analyst_reviews ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.analyst_reviews FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_analyst_reviews ON inspection.analyst_reviews USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.findings ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.findings FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_findings ON inspection.findings USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.verdicts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.verdicts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_verdicts ON inspection.verdicts USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.outbound_submissions ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    ALTER TABLE inspection.outbound_submissions FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    CREATE POLICY tenant_isolation_outbound_submissions ON inspection.outbound_submissions USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427211653_Add_RLS_Policies') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427211653_Add_RLS_Policies', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    CREATE TABLE inspection.scan_render_attempts (
        "Id" uuid NOT NULL,
        "ScanArtifactId" uuid NOT NULL,
        "Kind" character varying(32) NOT NULL,
        "AttemptCount" integer NOT NULL DEFAULT 0,
        "LastError" character varying(2000),
        "LastAttemptAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "PermanentlyFailedAt" timestamp with time zone,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_scan_render_attempts" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    CREATE INDEX ix_render_attempt_failed ON inspection.scan_render_attempts ("PermanentlyFailedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    CREATE INDEX ix_render_attempt_tenant ON inspection.scan_render_attempts ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    CREATE UNIQUE INDEX ux_render_attempt_artifact_kind ON inspection.scan_render_attempts ("ScanArtifactId", "Kind");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    ALTER TABLE inspection.scan_render_attempts ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    ALTER TABLE inspection.scan_render_attempts FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    CREATE POLICY tenant_isolation_scan_render_attempts ON inspection.scan_render_attempts USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427220330_Add_ScanRenderAttempt') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427220330_Add_ScanRenderAttempt', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221059_Add_NscimAppRole_Grants') THEN

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
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221059_Add_NscimAppRole_Grants') THEN
    GRANT USAGE ON SCHEMA inspection TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221059_Add_NscimAppRole_Grants') THEN
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA inspection TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221059_Add_NscimAppRole_Grants') THEN
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA inspection TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221059_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA inspection GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221059_Add_NscimAppRole_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA inspection GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260427221059_Add_NscimAppRole_Grants') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260427221059_Add_NscimAppRole_Grants', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104221_AddRuleEvaluations') THEN
    CREATE TABLE inspection.rule_evaluations (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "AuthorityCode" character varying(64) NOT NULL,
        "EvaluatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ViolationsJson" jsonb NOT NULL DEFAULT ('[]'::jsonb),
        "MutationsJson" jsonb NOT NULL DEFAULT ('[]'::jsonb),
        "ProviderErrorsJson" jsonb NOT NULL DEFAULT ('[]'::jsonb),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_rule_evaluations" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104221_AddRuleEvaluations') THEN
    CREATE INDEX ix_rule_eval_tenant_case_at ON inspection.rule_evaluations ("TenantId", "CaseId", "EvaluatedAt" DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104221_AddRuleEvaluations') THEN
    CREATE UNIQUE INDEX ux_rule_eval_tenant_case_authority ON inspection.rule_evaluations ("TenantId", "CaseId", "AuthorityCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104221_AddRuleEvaluations') THEN
    ALTER TABLE inspection.rule_evaluations ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104221_AddRuleEvaluations') THEN
    ALTER TABLE inspection.rule_evaluations FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104221_AddRuleEvaluations') THEN
    CREATE POLICY tenant_isolation_rule_evaluations ON inspection.rule_evaluations USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428104221_AddRuleEvaluations') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428104221_AddRuleEvaluations', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428130909_Grant_NscimApp_CreateOnSchema') THEN
    GRANT CREATE ON SCHEMA inspection TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260428130909_Grant_NscimApp_CreateOnSchema') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260428130909_Grant_NscimApp_CreateOnSchema', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE TABLE inspection.hs_commodity_reference (
        "TenantId" bigint NOT NULL,
        "Hs6" character(6) NOT NULL,
        "ZEffMin" numeric(4,2) NOT NULL,
        "ZEffMedian" numeric(4,2) NOT NULL,
        "ZEffMax" numeric(4,2) NOT NULL,
        "ZEffWindowMethod" character varying(32) NOT NULL,
        "ExpectedDensityKgPerM3" numeric(8,2),
        "DensityWindowKgPerM3" text,
        "TypicalPackaging" text[] NOT NULL DEFAULT ('{}'::text[]),
        "Confidence" integer NOT NULL,
        "SourcesJson" jsonb NOT NULL DEFAULT ('[]'::jsonb),
        "SampleCount" integer NOT NULL DEFAULT 0,
        "ScannerCalibrationVersionAtFitJson" jsonb,
        "LastValidatedAt" timestamp with time zone NOT NULL,
        "ValidatedByUserId" uuid,
        "NextReviewDueAt" timestamp with time zone NOT NULL,
        "Notes" character varying(2000),
        CONSTRAINT "PK_hs_commodity_reference" PRIMARY KEY ("TenantId", "Hs6")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE TABLE inspection.outcome_pull_cursors (
        "ExternalSystemInstanceId" uuid NOT NULL,
        "LastSuccessfulPullAt" timestamp with time zone NOT NULL,
        "LastPullWindowUntil" timestamp with time zone NOT NULL,
        "ConsecutiveFailures" integer NOT NULL DEFAULT 0,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_outcome_pull_cursors" PRIMARY KEY ("ExternalSystemInstanceId"),
        CONSTRAINT "FK_outcome_pull_cursors_external_system_instances_ExternalSyst~" FOREIGN KEY ("ExternalSystemInstanceId") REFERENCES inspection.external_system_instances ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE TABLE inspection.posthoc_rollout_phase (
        "Id" uuid NOT NULL,
        "TenantId" bigint NOT NULL,
        "ExternalSystemInstanceId" uuid NOT NULL,
        "CurrentPhase" integer NOT NULL,
        "PhaseEnteredAt" timestamp with time zone NOT NULL,
        "PromotedByUserId" uuid,
        "GateNotesJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        CONSTRAINT "PK_posthoc_rollout_phase" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_posthoc_rollout_phase_external_system_instances_ExternalSys~" FOREIGN KEY ("ExternalSystemInstanceId") REFERENCES inspection.external_system_instances ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE TABLE inspection.scanner_threshold_profiles (
        "Id" uuid NOT NULL,
        "ScannerDeviceInstanceId" uuid NOT NULL,
        "Version" integer NOT NULL,
        "ValuesJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "Status" integer NOT NULL,
        "EffectiveFrom" timestamp with time zone,
        "EffectiveTo" timestamp with time zone,
        "ProposedBy" integer NOT NULL,
        "ProposalRationaleJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "ApprovedByUserId" uuid,
        "ApprovedAt" timestamp with time zone,
        "ShadowStartedAt" timestamp with time zone,
        "ShadowCompletedAt" timestamp with time zone,
        "ShadowOutcomeJson" jsonb,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_scanner_threshold_profiles" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_scanner_threshold_profiles_scanner_device_instances_Scanner~" FOREIGN KEY ("ScannerDeviceInstanceId") REFERENCES inspection.scanner_device_instances ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE TABLE inspection.threat_library_provenance (
        "Id" uuid NOT NULL,
        "TenantId" bigint NOT NULL,
        "LocationId" uuid NOT NULL,
        "ThreatClass" integer NOT NULL,
        "ThreatSubclass" character varying(128),
        "SourceSeizureCaseId" uuid NOT NULL,
        "SourceVerdictId" uuid NOT NULL,
        "CaptureCaseId" uuid,
        "CapturedAt" timestamp with time zone NOT NULL,
        "CapturedByUserId" uuid NOT NULL,
        "SourceScannerInstanceId" uuid NOT NULL,
        "SourceScannerTypeCode" character varying(64) NOT NULL,
        "HePath" character varying(500) NOT NULL,
        "LePath" character varying(500) NOT NULL,
        "MaterialZeffPath" character varying(500) NOT NULL,
        "AlphaMaskPath" character varying(500) NOT NULL,
        "PoseCanonicalJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "TagsJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "Sam2ModelVersion" character varying(64) NOT NULL,
        "SegmentationQualityScore" numeric(4,3),
        "RedactionFlagsJson" jsonb NOT NULL DEFAULT ('{}'::jsonb),
        "LegalHoldStatus" integer NOT NULL,
        "Status" integer NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        CONSTRAINT "PK_threat_library_provenance" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_threat_library_provenance_locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES inspection.locations ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_threat_library_provenance_scanner_device_instances_SourceSc~" FOREIGN KEY ("SourceScannerInstanceId") REFERENCES inspection.scanner_device_instances ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_hs_commodity_sources_gin ON inspection.hs_commodity_reference USING gin ("SourcesJson");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_hs_commodity_tenant_inferred ON inspection.hs_commodity_reference ("TenantId", "Confidence") WHERE "Confidence" = 20;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_outcome_pull_cursors_tenant ON inspection.outcome_pull_cursors ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX "IX_posthoc_rollout_phase_ExternalSystemInstanceId" ON inspection.posthoc_rollout_phase ("ExternalSystemInstanceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE UNIQUE INDEX ux_posthoc_rollout_tenant_instance ON inspection.posthoc_rollout_phase ("TenantId", "ExternalSystemInstanceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_scanner_threshold_profiles_tenant ON inspection.scanner_threshold_profiles ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE UNIQUE INDEX ux_scanner_threshold_profiles_active ON inspection.scanner_threshold_profiles ("ScannerDeviceInstanceId") WHERE "Status" = 20;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE UNIQUE INDEX ux_scanner_threshold_profiles_tenant_scanner_version ON inspection.scanner_threshold_profiles ("TenantId", "ScannerDeviceInstanceId", "Version");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_threat_library_active ON inspection.threat_library_provenance ("TenantId", "Status") WHERE "Status" = 10;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_threat_library_legal_hold ON inspection.threat_library_provenance ("LegalHoldStatus") WHERE "LegalHoldStatus" = 10;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX "IX_threat_library_provenance_LocationId" ON inspection.threat_library_provenance ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX "IX_threat_library_provenance_SourceScannerInstanceId" ON inspection.threat_library_provenance ("SourceScannerInstanceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_threat_library_tenant ON inspection.threat_library_provenance ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE INDEX ix_threat_library_tenant_class_scanner_type ON inspection.threat_library_provenance ("TenantId", "ThreatClass", "SourceScannerTypeCode");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.scanner_threshold_profiles ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.scanner_threshold_profiles FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE POLICY tenant_isolation_scanner_threshold_profiles ON inspection.scanner_threshold_profiles USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.threat_library_provenance ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.threat_library_provenance FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE POLICY tenant_isolation_threat_library_provenance ON inspection.threat_library_provenance USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.hs_commodity_reference ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.hs_commodity_reference FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE POLICY tenant_isolation_hs_commodity_reference ON inspection.hs_commodity_reference USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.outcome_pull_cursors ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.outcome_pull_cursors FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE POLICY tenant_isolation_outcome_pull_cursors ON inspection.outcome_pull_cursors USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.posthoc_rollout_phase ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    ALTER TABLE inspection.posthoc_rollout_phase FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    CREATE POLICY tenant_isolation_posthoc_rollout_phase ON inspection.posthoc_rollout_phase USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429062458_Add_PhaseR3_TablesInferenceModernization') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429062458_Add_PhaseR3_TablesInferenceModernization', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow') THEN

      DELETE FROM inspection."__EFMigrationsHistory"
      WHERE "MigrationId" = '20260427164643_Add_ScanRenderArtifact';

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429063951_Cleanup_StaleScanRenderArtifactHistoryRow', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429064022_Drop_PublicEFMigrationsHistory') THEN
    DROP TABLE IF EXISTS public."__EFMigrationsHistory";
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429064022_Drop_PublicEFMigrationsHistory') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429064022_Drop_PublicEFMigrationsHistory', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    CREATE TABLE inspection.icums_signing_keys (
        "Id" uuid NOT NULL,
        "TenantId" bigint NOT NULL,
        "KeyId" character varying(32) NOT NULL,
        "KeyMaterialEncrypted" bytea NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ActivatedAt" timestamp with time zone,
        "RetiredAt" timestamp with time zone,
        "VerificationOnlyUntil" timestamp with time zone,
        CONSTRAINT "PK_icums_signing_keys" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    CREATE INDEX ix_icums_signing_keys_tenant_active ON inspection.icums_signing_keys ("TenantId", "ActivatedAt", "RetiredAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    CREATE UNIQUE INDEX ux_icums_signing_keys_tenant_keyid ON inspection.icums_signing_keys ("TenantId", "KeyId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    ALTER TABLE inspection.icums_signing_keys ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    ALTER TABLE inspection.icums_signing_keys FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    CREATE POLICY tenant_isolation_icums_signing_keys ON inspection.icums_signing_keys USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    REVOKE DELETE ON inspection.icums_signing_keys FROM nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429123406_Add_IcumsSigningKeys') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429123406_Add_IcumsSigningKeys', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429140000_BootstrapScannerThresholdProfilesV0') THEN

    INSERT INTO inspection.scanner_threshold_profiles (
        "Id",
        "ScannerDeviceInstanceId",
        "Version",
        "ValuesJson",
        "Status",
        "EffectiveFrom",
        "ProposedBy",
        "ProposalRationaleJson",
        "CreatedAt",
        "UpdatedAt",
        "TenantId"
    )
    SELECT
        gen_random_uuid(),
        s."Id",
        0,
        jsonb_build_object(
            'edge_detection',  jsonb_build_object('canny_low', 50, 'canny_high', 150),
            'normalization',   jsonb_build_object('percentile_low', 0.5, 'percentile_high', 99.5),
            'split_consensus', jsonb_build_object('disagreement_guard_px', 50),
            'watchdogs',       jsonb_build_object('pending_without_images_hours', 72),
            'decoder_limits',  jsonb_build_object('max_image_dim_px', 16384)
        ),
        20,                          -- ScannerThresholdProfileStatus.Active
        CURRENT_TIMESTAMP,
        0,                           -- ScannerThresholdProposalSource.Bootstrap
        jsonb_build_object('source', 'v1_hardcoded_values_2026_04_28'),
        CURRENT_TIMESTAMP,
        CURRENT_TIMESTAMP,
        s."TenantId"
    FROM inspection.scanner_device_instances s
    WHERE NOT EXISTS (
        SELECT 1
        FROM inspection.scanner_threshold_profiles p
        WHERE p."ScannerDeviceInstanceId" = s."Id"
          AND p."Version" = 0
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260429140000_BootstrapScannerThresholdProfilesV0') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429140000_BootstrapScannerThresholdProfilesV0', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260430111020_Add_PostHocOutcomeAdapter') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260430111020_Add_PostHocOutcomeAdapter', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE TABLE inspection.analysis_services (
        "Id" uuid NOT NULL,
        "Name" character varying(200) NOT NULL,
        "Description" character varying(2000),
        "IsBuiltInAllLocations" boolean NOT NULL DEFAULT FALSE,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "CreatedByUserId" uuid,
        "UpdatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_analysis_services" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE TABLE inspection.analysis_service_locations (
        "AnalysisServiceId" uuid NOT NULL,
        "LocationId" uuid NOT NULL,
        "AddedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_analysis_service_locations" PRIMARY KEY ("AnalysisServiceId", "LocationId"),
        CONSTRAINT "FK_analysis_service_locations_analysis_services_AnalysisServic~" FOREIGN KEY ("AnalysisServiceId") REFERENCES inspection.analysis_services ("Id") ON DELETE CASCADE,
        CONSTRAINT "FK_analysis_service_locations_locations_LocationId" FOREIGN KEY ("LocationId") REFERENCES inspection.locations ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE TABLE inspection.analysis_service_users (
        "AnalysisServiceId" uuid NOT NULL,
        "UserId" uuid NOT NULL,
        "AssignedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "AssignedByUserId" uuid,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_analysis_service_users" PRIMARY KEY ("AnalysisServiceId", "UserId"),
        CONSTRAINT "FK_analysis_service_users_analysis_services_AnalysisServiceId" FOREIGN KEY ("AnalysisServiceId") REFERENCES inspection.analysis_services ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE TABLE inspection.case_claims (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "AnalysisServiceId" uuid NOT NULL,
        "ClaimedByUserId" uuid NOT NULL,
        "ClaimedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ReleasedAt" timestamp with time zone,
        "ReleasedByUserId" uuid,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_case_claims" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_case_claims_analysis_services_AnalysisServiceId" FOREIGN KEY ("AnalysisServiceId") REFERENCES inspection.analysis_services ("Id") ON DELETE RESTRICT,
        CONSTRAINT "FK_case_claims_cases_CaseId" FOREIGN KEY ("CaseId") REFERENCES inspection.cases ("Id") ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE INDEX ix_analysis_service_locations_location ON inspection.analysis_service_locations ("LocationId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE INDEX ix_analysis_service_locations_tenant ON inspection.analysis_service_locations ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE INDEX ix_analysis_service_users_tenant ON inspection.analysis_service_users ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE INDEX ix_analysis_service_users_user ON inspection.analysis_service_users ("UserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE UNIQUE INDEX ux_analysis_services_tenant_built_in ON inspection.analysis_services ("TenantId") WHERE "IsBuiltInAllLocations" = TRUE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE UNIQUE INDEX ux_analysis_services_tenant_name ON inspection.analysis_services ("TenantId", "Name");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE INDEX ix_case_claims_service ON inspection.case_claims ("AnalysisServiceId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE INDEX ix_case_claims_tenant ON inspection.case_claims ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE UNIQUE INDEX ux_case_claims_active_per_case ON inspection.case_claims ("CaseId") WHERE "ReleasedAt" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."analysis_services" ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."analysis_services" FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE POLICY "tenant_isolation_analysis_services" ON "inspection"."analysis_services" USING (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint ) WITH CHECK (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."analysis_service_locations" ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."analysis_service_locations" FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE POLICY "tenant_isolation_analysis_service_locations" ON "inspection"."analysis_service_locations" USING (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint ) WITH CHECK (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."analysis_service_users" ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."analysis_service_users" FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE POLICY "tenant_isolation_analysis_service_users" ON "inspection"."analysis_service_users" USING (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint ) WITH CHECK (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."case_claims" ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    ALTER TABLE "inspection"."case_claims" FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    CREATE POLICY "tenant_isolation_case_claims" ON "inspection"."case_claims" USING (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint ) WITH CHECK (  "TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN

    CREATE OR REPLACE FUNCTION inspection.fn_prevent_built_in_all_locations_delete()
    RETURNS TRIGGER AS $$
    BEGIN
        IF OLD."IsBuiltInAllLocations" = TRUE THEN
            RAISE EXCEPTION 'Cannot delete the built-in "All Locations" AnalysisService (tenant_id=%, service_id=%)',
                OLD."TenantId", OLD."Id";
        END IF;
        RETURN OLD;
    END;
    $$ LANGUAGE plpgsql;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN

    CREATE TRIGGER trg_prevent_built_in_all_locations_delete
        BEFORE DELETE ON inspection.analysis_services
        FOR EACH ROW
        EXECUTE FUNCTION inspection.fn_prevent_built_in_all_locations_delete();
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165612_Add_AnalysisServiceVp6') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260502165612_Add_AnalysisServiceVp6', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165738_BootstrapAnalysisServicesV0') THEN

    INSERT INTO inspection.analysis_services (
        "Id",
        "Name",
        "Description",
        "IsBuiltInAllLocations",
        "CreatedAt",
        "CreatedByUserId",
        "UpdatedAt",
        "TenantId"
    )
    SELECT
        gen_random_uuid(),
        'All Locations',
        'Built-in service that includes every location in the tenant. Cannot be deleted; admins manage analyst access via membership.',
        TRUE,
        CURRENT_TIMESTAMP,
        NULL,
        CURRENT_TIMESTAMP,
        t."TenantId"
    FROM (
        SELECT DISTINCT "TenantId" FROM inspection.locations
        UNION
        SELECT DISTINCT "TenantId" FROM inspection.cases
    ) t
    WHERE NOT EXISTS (
        SELECT 1
        FROM inspection.analysis_services s
        WHERE s."TenantId" = t."TenantId"
          AND s."IsBuiltInAllLocations" = TRUE
    );

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165738_BootstrapAnalysisServicesV0') THEN

    INSERT INTO inspection.analysis_service_locations (
        "AnalysisServiceId",
        "LocationId",
        "AddedAt",
        "TenantId"
    )
    SELECT
        s."Id",
        l."Id",
        CURRENT_TIMESTAMP,
        l."TenantId"
    FROM inspection.locations l
    JOIN inspection.analysis_services s
        ON s."TenantId" = l."TenantId"
       AND s."IsBuiltInAllLocations" = TRUE
    WHERE NOT EXISTS (
        SELECT 1
        FROM inspection.analysis_service_locations asl
        WHERE asl."AnalysisServiceId" = s."Id"
          AND asl."LocationId" = l."Id"
    );

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260502165738_BootstrapAnalysisServicesV0') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260502165738_BootstrapAnalysisServicesV0', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260504113030_Add_ExternalSystemSubsetScope') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260504113030_Add_ExternalSystemSubsetScope', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260504160000_Add_OutboundSubmissionPriority') THEN
    ALTER TABLE inspection.outbound_submissions ADD "Priority" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260504160000_Add_OutboundSubmissionPriority') THEN
    ALTER TABLE inspection.outbound_submissions ADD "LastAttemptAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260504160000_Add_OutboundSubmissionPriority') THEN
    CREATE INDEX ix_outbound_tenant_status_priority_time ON inspection.outbound_submissions ("TenantId", "Status", "Priority" DESC, "SubmittedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260504160000_Add_OutboundSubmissionPriority') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260504160000_Add_OutboundSubmissionPriority', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE TABLE inspection.cross_record_detection (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "DetectedAt" timestamp with time zone NOT NULL,
        "DetectorVersion" character varying(32) NOT NULL,
        "State" integer NOT NULL,
        "DetectedSubjectsJson" jsonb NOT NULL DEFAULT ('[]'::jsonb),
        "SplitCaseIdsJson" jsonb,
        "Notes" character varying(2000),
        "ReviewedByUserId" uuid,
        "ReviewedAt" timestamp with time zone,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_cross_record_detection" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE TABLE inspection.sla_window (
        "Id" uuid NOT NULL,
        "CaseId" uuid NOT NULL,
        "WindowName" character varying(128) NOT NULL,
        "StartedAt" timestamp with time zone NOT NULL,
        "DueAt" timestamp with time zone NOT NULL,
        "ClosedAt" timestamp with time zone,
        "State" integer NOT NULL,
        "BudgetMinutes" integer NOT NULL,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_sla_window" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE INDEX ix_cross_record_detection_tenant_case ON inspection.cross_record_detection ("TenantId", "CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE INDEX ix_cross_record_detection_tenant_state_detected ON inspection.cross_record_detection ("TenantId", "State", "DetectedAt" DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE UNIQUE INDEX ux_cross_record_detection_case_version ON inspection.cross_record_detection ("CaseId", "DetectorVersion");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE INDEX ix_sla_window_tenant_case ON inspection.sla_window ("TenantId", "CaseId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE INDEX ix_sla_window_tenant_state_due ON inspection.sla_window ("TenantId", "State", "DueAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE UNIQUE INDEX ux_sla_window_open_per_case_window ON inspection.sla_window ("CaseId", "WindowName") WHERE "ClosedAt" IS NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    ALTER TABLE inspection.sla_window ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    ALTER TABLE inspection.sla_window FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE POLICY tenant_isolation_sla_window ON inspection.sla_window USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    ALTER TABLE inspection.cross_record_detection ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    ALTER TABLE inspection.cross_record_detection FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    CREATE POLICY tenant_isolation_cross_record_detection ON inspection.cross_record_detection USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN

    GRANT SELECT, INSERT, UPDATE ON inspection.sla_window TO nscim_app;
    GRANT SELECT, INSERT, UPDATE, DELETE ON inspection.cross_record_detection TO nscim_app;

    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505073742_Add_SlaWindow_And_CrossRecordDetection') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260505073742_Add_SlaWindow_And_CrossRecordDetection', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505100000_Add_OutboundSubmissionRetry') THEN
    ALTER TABLE inspection.outbound_submissions ADD "RetryCount" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505100000_Add_OutboundSubmissionRetry') THEN
    ALTER TABLE inspection.outbound_submissions ADD "NextAttemptAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505100000_Add_OutboundSubmissionRetry') THEN
    CREATE INDEX ix_outbound_tenant_status_next_attempt ON inspection.outbound_submissions ("TenantId", "Status", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505100000_Add_OutboundSubmissionRetry') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260505100000_Add_OutboundSubmissionRetry', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    ALTER TABLE inspection.cases ADD "ReviewQueue" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    ALTER TABLE inspection.analyst_reviews ADD "CompletedAt" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    ALTER TABLE inspection.analyst_reviews ADD "Outcome" character varying(64);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    ALTER TABLE inspection.analyst_reviews ADD "ReviewType" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    ALTER TABLE inspection.analyst_reviews ADD "StartedByUserId" uuid;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    CREATE INDEX ix_cases_tenant_queue_state_time ON inspection.cases ("TenantId", "ReviewQueue" DESC, "State", "OpenedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    CREATE INDEX ix_analyst_reviews_tenant_type_time ON inspection.analyst_reviews ("TenantId", "ReviewType", "CreatedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM inspection."__EFMigrationsHistory" WHERE "MigrationId" = '20260505114543_Add_ReviewType') THEN
    INSERT INTO inspection."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260505114543_Add_ReviewType', '10.0.7');
    END IF;
END $EF$;
COMMIT;

