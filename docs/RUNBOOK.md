# NickERP v2 — Operations Runbook

This runbook captures one-off operational notes that ops engineers may
need when applying migrations, deploying services, or recovering from
incidents. Sprint-7 will expand it; for now it carries only items that
have shipped.

## Migration cleanups

### FU-6 — drop pre-H3 `public.__EFMigrationsHistory` remnants

H3 (Sprint 2 hardening) relocated EF Core's migration history out of
the `public` schema into per-context schemas: `audit.__EFMigrationsHistory`,
`identity.__EFMigrationsHistory`, `tenancy.__EFMigrationsHistory` (all
in `nickerp_platform`), and `inspection.__EFMigrationsHistory` (in
`nickerp_inspection`). The original `public.__EFMigrationsHistory`
copy was deliberately kept around through the cutover as a rollback
safety net.

The FU-6 migrations (`Drop_PublicEFMigrationsHistory`, attached to
`AuditDbContext` for the platform DB and `InspectionDbContext` for the
inspection DB) drop those orphan tables via
`DROP TABLE IF EXISTS public."__EFMigrationsHistory";`. The migration
is safe to apply against any post-H3 environment: on a fresh install
that never carried a `public` history table the `IF EXISTS` makes the
statement a no-op; on an upgrade install it removes the dead remnant.

**Rollback.** The `Down` method is intentionally a no-op — the pre-H3
rows are not preserved (the H3 relocate step rebuilt the per-context
tables from the migration filesystem rather than copying), so there is
nothing meaningful to restore. A true rollback past this migration
would have to repopulate `public.__EFMigrationsHistory` from a
database backup taken before this point. In practice the migration is
not expected to need rollback: the data dropped is metadata that EF
Core no longer consults.

**Verification.** After applying:

```bash
psql -U postgres -d nickerp_platform -c '\dt public."__EFMigrationsHistory"'
psql -U postgres -d nickerp_inspection -c '\dt public."__EFMigrationsHistory"'
```

Both should return `Did not find any relation`. The per-context
tables (`audit.__EFMigrationsHistory`, `inspection.__EFMigrationsHistory`,
etc.) remain intact and continue to drive `Database.Migrate()`.
