using System.Text.Json;

namespace NickERP.Platform.Tenancy.Entities;

/// <summary>
/// Sprint 18 — append-only record of every hard-purge operation. Lives
/// in <c>tenancy.tenant_purge_log</c> in <c>nickerp_platform</c>.
/// </summary>
/// <remarks>
/// <para>
/// Purpose: when <c>TenantPurgeOrchestrator</c> hard-purges a tenant, the
/// originating tenant's <c>audit.events</c> rows are deleted along with
/// everything else. The purge itself therefore can't be audited via
/// <c>audit.events</c> in that tenant — and recording it under
/// <c>TenantId = -1</c> in audit.events would require every purge to
/// run in system-context (which we deliberately scope narrowly per
/// <c>docs/system-context-audit-register.md</c>). Instead we keep a
/// dedicated, NOT-tenant-scoped log table that survives every tenant
/// purge: <c>tenant_purge_log</c>.
/// </para>
/// <para>
/// Not under RLS. Cross-tenant by design — operator dashboards must see
/// every purge across the suite.
/// </para>
/// </remarks>
public sealed class TenantPurgeLog
{
    /// <summary>Server-assigned primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The tenant id that was hard-purged. Records the long
    /// even though the row in <c>tenancy.tenants</c> is gone.</summary>
    public long TenantId { get; set; }

    /// <summary>The short code of the purged tenant at the time of the
    /// purge — preserved for human-readable dashboards even after the
    /// tenants row is gone.</summary>
    public string TenantCode { get; set; } = string.Empty;

    /// <summary>The display name at the time of the purge.</summary>
    public string TenantName { get; set; } = string.Empty;

    /// <summary>Wallclock at which the purge completed.</summary>
    public DateTimeOffset PurgedAt { get; set; }

    /// <summary>Identity user id of the operator who confirmed the
    /// purge in the admin UI. Required — hard-purge always has a
    /// human in the loop.</summary>
    public Guid PurgedByUserId { get; set; }

    /// <summary>The reason captured at soft-delete time, copied here
    /// for the historical record.</summary>
    public string? DeletionReason { get; set; }

    /// <summary>Wallclock at which the soft-delete first happened
    /// (i.e. the start of the retention window). Carried so admins can
    /// audit the time-from-delete-to-purge spread.</summary>
    public DateTimeOffset? SoftDeletedAt { get; set; }

    /// <summary>Per-table row counts of what was deleted, captured by
    /// the orchestrator. JSON for forward-compat: today the keys are
    /// schema-qualified table names (e.g. <c>inspection.cases</c>),
    /// values are the rowcount.</summary>
    public JsonElement RowCounts { get; set; }

    /// <summary>Free-text orchestrator status: <c>"completed"</c> when
    /// every step ran, <c>"partial"</c> when one or more steps failed
    /// (with the failed-table name in <see cref="FailureNote"/>).</summary>
    public string Outcome { get; set; } = "completed";

    /// <summary>Populated when <see cref="Outcome"/> is partial — the
    /// concise reason the operator can act on without crawling logs.</summary>
    public string? FailureNote { get; set; }
}
