-- ----------------------------------------------------------------------------
-- audit-events-create-partition.sql
--
-- Sprint 52 / FU-audit-events-partitioning — recurring helper that
-- creates the next month's partition of audit.events. Idempotent: a
-- second run for the same month is a no-op.
--
-- Companion to migration
-- 20260505233200_Convert_AuditEvents_To_Partitioned_Table.cs.
--
-- Wire this into a recurring schedule:
--   - Linux:  /etc/cron.d/nickerp-audit-partitions  (1 * 25 * *)
--   - Windows Scheduled Task: monthly, 25th of the month, 03:00 UTC
--   - Pilot deploy: invoke via tools/migration-runner with
--     `--script=tools/migrations/audit-events-create-partition.sql`
--
-- Why "25th of the month": the migration pre-creates 6 months ahead;
-- a monthly run that lands on the 25th adds another month every month,
-- keeping ~6 months of forward coverage at all times. If the run is
-- ever skipped, the next month is still pre-created — the system
-- degrades gracefully toward "no future partition" only if the run
-- skips ~6 consecutive months.
--
-- Operator note: this script must run as the postgres superuser (or
-- a role with CREATE on the audit schema). nscim_app does NOT have
-- DDL privileges by design.
-- ----------------------------------------------------------------------------

DO $$
DECLARE
    -- Compute the partition immediately AFTER the latest existing one.
    -- This is robust to delayed runs (catches up across multiple
    -- months) and to manual operator intervention.
    target_month_start  date;
    target_month_end    date;
    partition_name      text;
    latest_upper_bound  text;
    grant_role          text := 'nscim_app';
BEGIN
    -- Find the latest pre-existing partition's upper bound. We parse
    -- pg_get_expr(relpartbound) which yields a string like:
    --   FOR VALUES FROM ('2026-04-01 00:00:00+00') TO ('2026-05-01 00:00:00+00')
    -- and extract the TO timestamp; the next partition starts there.
    SELECT pg_get_expr(c.relpartbound, c.oid)
      INTO latest_upper_bound
      FROM pg_inherits i
      JOIN pg_class p ON p.oid = i.inhparent
      JOIN pg_namespace pn ON pn.oid = p.relnamespace
      JOIN pg_class c ON c.oid = i.inhrelid
     WHERE pn.nspname = 'audit'
       AND p.relname  = 'events'
     ORDER BY pg_get_expr(c.relpartbound, c.oid) DESC
     LIMIT 1;

    IF latest_upper_bound IS NULL THEN
        -- No partitions yet — bail. This means the migration
        -- 20260505233200_Convert_AuditEvents_To_Partitioned_Table has
        -- not yet run. Nothing to do; the migration creates the
        -- starting set.
        RAISE NOTICE 'audit.events has no partitions yet; migration must run first. Aborting.';
        RETURN;
    END IF;

    -- Pull the second timestamp out of the FOR VALUES clause. The
    -- substring captures whatever is between the second pair of
    -- quotes — robust against the +00 / +0000 / no-tz variants
    -- because we compare on date arithmetic afterwards.
    target_month_start := substring(
        latest_upper_bound,
        'TO \(''([0-9-]+) [0-9:]+'
    )::date;

    IF target_month_start IS NULL THEN
        RAISE EXCEPTION
            'Could not parse upper bound of latest partition: %',
            latest_upper_bound;
    END IF;

    target_month_end := target_month_start + INTERVAL '1 month';
    partition_name   := format('events_%s', to_char(target_month_start, 'YYYY_MM'));

    -- Idempotency: if the partition already exists (e.g. someone ran
    -- this script twice in the same month), do nothing.
    IF EXISTS (
        SELECT 1
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
         WHERE n.nspname = 'audit'
           AND c.relname  = partition_name
    ) THEN
        RAISE NOTICE 'Partition audit.% already exists; nothing to do.', partition_name;
        RETURN;
    END IF;

    EXECUTE format(
        $f$CREATE TABLE audit.%I PARTITION OF audit.events FOR VALUES FROM (%L) TO (%L);$f$,
        partition_name,
        target_month_start::text || ' 00:00:00+00',
        target_month_end::text   || ' 00:00:00+00'
    );

    -- Per-partition grant. ALTER DEFAULT PRIVILEGES catches new
    -- TABLEs in the schema, but the partition-of relationship
    -- inherits the parent's grants only for SELECT through the
    -- parent — explicit GRANT is required for INSERT routing to
    -- land on the partition without a permission failure.
    EXECUTE format(
        $f$GRANT SELECT, INSERT ON audit.%I TO %I;$f$,
        partition_name,
        grant_role
    );

    RAISE NOTICE 'Created partition audit.% covering [%, %).',
        partition_name, target_month_start, target_month_end;
END
$$;
