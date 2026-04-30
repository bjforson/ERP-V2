DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'nickfinance') THEN
        CREATE SCHEMA nickfinance;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS nickfinance."__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'nickfinance') THEN
            CREATE SCHEMA nickfinance;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE TABLE nickfinance.fx_rate (
        "FromCurrency" character varying(3) NOT NULL,
        "ToCurrency" character varying(3) NOT NULL,
        "EffectiveDate" date NOT NULL,
        "TenantId" bigint,
        "Rate" numeric(18,8) NOT NULL,
        "PublishedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "PublishedByUserId" uuid NOT NULL,
        CONSTRAINT "PK_fx_rate" PRIMARY KEY ("FromCurrency", "ToCurrency", "EffectiveDate"),
        CONSTRAINT ck_fx_rate_positive CHECK ("Rate" > 0)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE TABLE nickfinance.petty_cash_boxes (
        "Id" uuid NOT NULL,
        "Code" character varying(64) NOT NULL,
        "Name" character varying(200) NOT NULL,
        "CurrencyCode" character varying(3) NOT NULL,
        "CustodianUserId" uuid NOT NULL,
        "ApproverUserId" uuid NOT NULL,
        "OpeningBalanceAmount" numeric(18,4) NOT NULL DEFAULT 0.0,
        "OpeningBalanceCurrency" character varying(3) NOT NULL,
        "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ArchivedAt" timestamp with time zone,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_petty_cash_boxes" PRIMARY KEY ("Id"),
        CONSTRAINT ck_petty_cash_boxes_custodian_neq_approver CHECK ("CustodianUserId" <> "ApproverUserId"),
        CONSTRAINT ck_petty_cash_boxes_opening_currency_match CHECK ("OpeningBalanceCurrency" = "CurrencyCode")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE TABLE nickfinance.petty_cash_ledger_events (
        "Id" uuid NOT NULL,
        "BoxId" uuid NOT NULL,
        "VoucherId" uuid,
        "EventType" integer NOT NULL,
        "Direction" integer NOT NULL,
        "AmountNative" numeric(18,4) NOT NULL,
        "CurrencyNative" character varying(3) NOT NULL,
        "AmountBase" numeric(18,4) NOT NULL,
        "CurrencyBase" character varying(3) NOT NULL,
        "FxRate" numeric(18,8) NOT NULL,
        "FxRateDate" date NOT NULL,
        "PostedAt" timestamp with time zone NOT NULL,
        "PostedByUserId" uuid NOT NULL,
        "CorrectsEventId" uuid,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_petty_cash_ledger_events" PRIMARY KEY ("Id"),
        CONSTRAINT ck_ledger_amount_base_nonneg CHECK ("AmountBase" >= 0),
        CONSTRAINT ck_ledger_amount_native_nonneg CHECK ("AmountNative" >= 0)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE TABLE nickfinance.petty_cash_periods (
        "TenantId" bigint NOT NULL,
        "PeriodYearMonth" character varying(7) NOT NULL,
        "ClosedAt" timestamp with time zone,
        "ClosedByUserId" uuid,
        CONSTRAINT "PK_petty_cash_periods" PRIMARY KEY ("TenantId", "PeriodYearMonth")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE TABLE nickfinance.petty_cash_vouchers (
        "Id" uuid NOT NULL,
        "BoxId" uuid NOT NULL,
        "SequenceNumber" bigint NOT NULL,
        "State" integer NOT NULL,
        "Purpose" character varying(2000) NOT NULL,
        "RequestedAmount" numeric(18,4) NOT NULL,
        "RequestedCurrency" character varying(3) NOT NULL,
        "RequestedAmountBase" numeric(18,4) NOT NULL,
        "RequestedCurrencyBase" character varying(3) NOT NULL,
        "RequestedByUserId" uuid NOT NULL,
        "RequestedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
        "ApproverUserId" uuid,
        "ApprovedAt" timestamp with time zone,
        "DisbursedAmount" numeric(18,4),
        "DisbursedCurrency" character varying(3),
        "DisbursedAmountBase" numeric(18,4),
        "DisbursedCurrencyBase" character varying(3),
        "DisbursedAt" timestamp with time zone,
        "ReconciledAt" timestamp with time zone,
        "RejectedReason" character varying(2000),
        "CancelledAt" timestamp with time zone,
        "TenantId" bigint NOT NULL,
        CONSTRAINT "PK_petty_cash_vouchers" PRIMARY KEY ("Id"),
        CONSTRAINT "FK_petty_cash_vouchers_petty_cash_boxes_BoxId" FOREIGN KEY ("BoxId") REFERENCES nickfinance.petty_cash_boxes ("Id") ON DELETE RESTRICT
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX ix_fx_rate_pair_effective_desc ON nickfinance.fx_rate ("FromCurrency", "ToCurrency", "EffectiveDate" DESC);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX ix_petty_cash_boxes_tenant ON nickfinance.petty_cash_boxes ("TenantId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE UNIQUE INDEX ux_petty_cash_boxes_tenant_code ON nickfinance.petty_cash_boxes ("TenantId", "Code");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX ix_ledger_corrects ON nickfinance.petty_cash_ledger_events ("CorrectsEventId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX ix_ledger_tenant_box_posted ON nickfinance.petty_cash_ledger_events ("TenantId", "BoxId", "PostedAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX ix_ledger_voucher ON nickfinance.petty_cash_ledger_events ("VoucherId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX "IX_petty_cash_vouchers_BoxId" ON nickfinance.petty_cash_vouchers ("BoxId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX ix_petty_cash_vouchers_requester ON nickfinance.petty_cash_vouchers ("RequestedByUserId");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE INDEX ix_petty_cash_vouchers_tenant_box_state ON nickfinance.petty_cash_vouchers ("TenantId", "BoxId", "State");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    CREATE UNIQUE INDEX ux_petty_cash_vouchers_tenant_box_seq ON nickfinance.petty_cash_vouchers ("TenantId", "BoxId", "SequenceNumber");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131827_Init_NickFinance') THEN
    INSERT INTO nickfinance."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429131827_Init_NickFinance', '10.0.7');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_boxes ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_boxes FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    CREATE POLICY tenant_isolation_petty_cash_boxes ON nickfinance.petty_cash_boxes USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_vouchers ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_vouchers FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    CREATE POLICY tenant_isolation_petty_cash_vouchers ON nickfinance.petty_cash_vouchers USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_ledger_events ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_ledger_events FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    CREATE POLICY tenant_isolation_petty_cash_ledger_events ON nickfinance.petty_cash_ledger_events USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_periods ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.petty_cash_periods FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    CREATE POLICY tenant_isolation_petty_cash_periods ON nickfinance.petty_cash_periods USING ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) WITH CHECK ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.fx_rate ENABLE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER TABLE nickfinance.fx_rate FORCE ROW LEVEL SECURITY;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    CREATE POLICY tenant_isolation_fx_rate ON nickfinance.fx_rate USING (("TenantId" IS NULL) OR ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint) OR (current_setting('app.tenant_id', true) = '-1')) WITH CHECK ((current_setting('app.tenant_id', true) = '-1' AND "TenantId" IS NULL) OR ("TenantId" = COALESCE(current_setting('app.tenant_id', true), '0')::bigint));
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN

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
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    GRANT USAGE ON SCHEMA nickfinance TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA nickfinance TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA nickfinance TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA nickfinance GRANT SELECT, INSERT, UPDATE ON TABLES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    ALTER DEFAULT PRIVILEGES IN SCHEMA nickfinance GRANT USAGE, SELECT ON SEQUENCES TO nscim_app;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM nickfinance."__EFMigrationsHistory" WHERE "MigrationId" = '20260429131858_Add_RLS_And_Grants') THEN
    INSERT INTO nickfinance."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260429131858_Add_RLS_And_Grants', '10.0.7');
    END IF;
END $EF$;
COMMIT;

