using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 13 / §6.11 — model-snapshot reconciliation for the
    /// <c>OutcomePullCursor</c> + <c>PostHocRolloutPhase</c> entities.
    ///
    /// <para>
    /// The two tables (<c>inspection.outcome_pull_cursors</c> +
    /// <c>inspection.posthoc_rollout_phase</c>) were CREATED by the
    /// earlier migration <c>20260429062458_Add_PhaseR3_TablesInferenceModernization</c>,
    /// which also installed RLS + force-enabled it + created the
    /// <c>tenant_isolation_*</c> policies. R3 created the schema but
    /// did not wire <c>OnModelCreating</c> mappings — the same drift
    /// Sprint 12 fixed for <c>ScannerThresholdProfile</c>.
    /// </para>
    ///
    /// <para>
    /// Sprint 13 adds <c>DbSet</c>s + <c>OnModelCreating</c> blocks for
    /// both entities so the worker can query them via EF, which forces
    /// the snapshot to acquire entries for them. EF, faced with
    /// <c>OnModelCreating</c> entries that aren't in the snapshot,
    /// generates this migration with full <c>CreateTable</c> +
    /// <c>CreateIndex</c> calls — but the live DB already has the
    /// tables, so the generated DDL would fail on apply. We empty the
    /// body to make this a pure snapshot-reconciliation migration —
    /// the Designer file (which IS the snapshot delta) does the real
    /// work; the <c>Up</c> / <c>Down</c> stay empty.
    /// </para>
    ///
    /// <para>
    /// On a brand-new dev DB seeded from scratch, the R3 migration is
    /// applied first and creates the tables. This migration runs after
    /// and is a NoOp at the SQL layer. The <c>__EFMigrationsHistory</c>
    /// row is still inserted by EF, marking the migration as applied so
    /// the next migration can advance the chain.
    /// </para>
    /// </summary>
    public partial class Add_PostHocOutcomeAdapter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see class summary. The R3 migration
            // (Add_PhaseR3_TablesInferenceModernization) already created
            // both tables, indexes, and tenant_isolation_* RLS policies.
            // This migration exists only to bring the EF model snapshot
            // into agreement with the OnModelCreating blocks added in
            // Sprint 13.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — paired with the empty Up above. The
            // R3 migration's Down drops both tables; rolling back to a
            // pre-R3 state goes through that migration's Down, not this
            // one.
        }
    }
}
