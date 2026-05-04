using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NickERP.Inspection.Database.Migrations
{
    /// <summary>
    /// Sprint 16 / LA1 extension (locked 2026-05-02) — extend the
    /// <c>ExternalSystemBindingScope</c> enum from a binary
    /// PerLocation/Shared distinction to a three-way distinction:
    /// <c>PerLocation = 0</c>, <c>Shared = 1</c>, <c>SubsetOfLocations = 2</c>.
    ///
    /// <para>
    /// **Storage no-op.** The <c>scope</c> column on
    /// <c>inspection.external_system_instances</c> is already
    /// <c>integer NOT NULL</c> (mapped via <c>HasConversion&lt;int&gt;()</c>);
    /// the new enum value <c>2</c> stores in the same column without
    /// any DDL change. The marker migration exists so:
    /// <list type="number">
    ///   <item><description>Future hands have a deployment-history breadcrumb pointing to the Sprint 16 source change.</description></item>
    ///   <item><description>The <c>InspectionDbContextModelSnapshot</c> stays in sync with the post-extension model (`MaxLength`/`HasConversion` shape unchanged but EF re-emits the snapshot on every <c>migrations add</c>; running <c>migrations script &lt;previous&gt; Add_ExternalSystemSubsetScope</c> produces an empty diff that operators can apply without ceremony).</description></item>
    /// </list>
    /// No data backfill required — existing rows keep their integer
    /// values (PerLocation = 0, Shared = 1) untouched.
    /// </para>
    ///
    /// <para>
    /// **Validation.** Per-scope cardinality (PerLocation = 1 binding,
    /// SubsetOfLocations >= 2 bindings, Shared = 0 bindings) is enforced
    /// in the application layer by
    /// <c>NickERP.Inspection.Core.Entities.ExternalSystemBindingScopeValidation</c>
    /// rather than as a DB CHECK constraint, because the rule is
    /// "count of binding rows" — Postgres can't express that as a
    /// per-row CHECK without a trigger and a trigger would not buy us
    /// anything stronger than the application-layer guard the admin UI
    /// + service already enforce.
    /// </para>
    /// </summary>
    public partial class Add_ExternalSystemSubsetScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see class XML comment.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — the column shape did not change. A
            // rollback of the C# enum (re-removing SubsetOfLocations)
            // does not require any DB DDL either; rows already storing
            // value `2` would become orphan integers, but that's a
            // code-side rollback concern, not a migration concern.
        }
    }
}
