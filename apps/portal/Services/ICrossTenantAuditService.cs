using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Portal.Services;

/// <summary>
/// Sprint 56 / FU-cross-tenant-aggregation — read-only cross-tenant
/// aggregator that powers the platform-admin Audit Dashboard at
/// <c>/admin/audit-dashboard</c>.
///
/// <para>
/// Sprint 33's <c>ReportsService</c> surfaces per-tenant audit metrics
/// through the inspection module's per-tenant DbContext. Platform admins
/// running a multi-tenant pilot need a global view to spot anomalies
/// across the whole deployment — which tenant generates the most
/// <c>*.error</c> events, which tenant's webhook cursor is replay-stalling,
/// how many tenants are active in the window. This interface is the
/// portal-side surface for that view.
/// </para>
///
/// <para>
/// <b>Pattern:</b> per-tenant fan-out (Sprint 36 <c>SlaStateRefresherWorker</c>
/// + Sprint 44 <c>RetentionEnforcerWorker</c>). Discovery via
/// <see cref="NickERP.Platform.Tenancy.Database.TenancyDbContext.Tenants"/>
/// (no RLS — root of the tenant graph), then for each active tenant
/// the implementation flips the request-scope <c>ITenantContext</c> via
/// <c>SetTenant</c> and reads the audit DbContext under the per-tenant
/// RLS narrowing the existing infra already provides. <b>No new
/// <c>SetSystemContext</c> caller</b> — the per-tenant fan-out reuses
/// the existing RLS posture without broadening it.
/// </para>
///
/// <para>
/// <b>Aggressive caching.</b> Cross-tenant queries fan out per-tenant —
/// at N tenants and per-tick cost ~N×(audit-events count + grouped
/// count), the per-tick load grows linearly with tenant count. The
/// dashboard's auto-refresh sits on top of a 60 s in-memory cache; the
/// dashboard's "Refresh" button can ignore the cache by passing
/// <see cref="CrossTenantAuditQueryOptions.SkipCache"/>.
/// </para>
///
/// <para>
/// <b>Partition pruning.</b> Sprint 52 partitioned <c>audit.events</c>
/// by <c>OccurredAt</c>; every query the implementation issues includes
/// an explicit <c>OccurredAt &gt;= window-from</c> filter so Postgres
/// can prune partitions and stay off the historical heap.
/// </para>
/// </summary>
public interface ICrossTenantAuditService
{
    /// <summary>
    /// Per-tenant summary rows for the dashboard's "All-tenants" card.
    /// Returns one row per active tenant covering the requested
    /// <paramref name="window"/>.
    /// </summary>
    /// <param name="window">Trailing window. The implementation clamps to
    /// the configured <see cref="CrossTenantAuditOptions.MaxWindow"/>
    /// (default 30 days) and floors at 1 hour.</param>
    /// <param name="ct">Cancellation.</param>
    Task<IReadOnlyList<CrossTenantAuditSummaryRow>> GetCrossTenantSummaryAsync(
        TimeSpan window,
        CancellationToken ct = default);

    /// <summary>
    /// The N tenants with the highest <i>error rate</i> (error count /
    /// total event count, ties broken by total error count) over the
    /// requested window. Tenants with zero events are excluded so the
    /// "noisy idle tenant" doesn't crowd out a real outlier.
    /// </summary>
    Task<IReadOnlyList<CrossTenantAuditTopErrorRow>> GetCrossTenantTopErrorsAsync(
        TimeSpan window,
        int take,
        CancellationToken ct = default);

    /// <summary>
    /// Per-tenant webhook-cursor health. One row per (tenant, adapter)
    /// pair recorded in <see cref="NickERP.Inspection.Core.Entities.WebhookCursor"/>;
    /// <c>LagSeconds</c> is the wall-clock difference between the
    /// cursor's last-update time and the latest matching audit event.
    /// Today this surface is empty in production (no plugin adapters
    /// ship yet); the dashboard uses the empty-shape contract to render
    /// an "no adapters configured" placeholder rather than a crash.
    /// </summary>
    Task<IReadOnlyList<CrossTenantWebhookHealthRow>> GetCrossTenantWebhookHealthAsync(
        CancellationToken ct = default);

    /// <summary>
    /// Drill-down for a single tenant — wraps the same per-tenant fan-out
    /// for <see cref="TenantDetail"/>'s "Audit activity" card. Returns
    /// the most-recent <paramref name="take"/> rows for the tenant in
    /// the trailing <paramref name="window"/>, optionally filtered by
    /// event-type substring + minimum severity (events whose type
    /// contains <c>.error</c>).
    /// </summary>
    Task<TenantAuditActivity> GetTenantAuditActivityAsync(
        long tenantId,
        TimeSpan window,
        int take,
        TenantAuditActivityFilter? filter = null,
        CancellationToken ct = default);
}

/// <summary>
/// One row in <see cref="ICrossTenantAuditService.GetCrossTenantSummaryAsync"/>.
/// </summary>
public sealed record CrossTenantAuditSummaryRow(
    long TenantId,
    string TenantCode,
    string TenantName,
    int EventCount,
    int ErrorCount,
    DateTimeOffset? LastEventAt);

/// <summary>
/// One row in <see cref="ICrossTenantAuditService.GetCrossTenantTopErrorsAsync"/>.
/// </summary>
/// <param name="ErrorRate">Errors / total events, in the [0, 1] range.</param>
public sealed record CrossTenantAuditTopErrorRow(
    long TenantId,
    string TenantCode,
    string TenantName,
    int ErrorCount,
    int EventCount,
    double ErrorRate);

/// <summary>
/// One row in <see cref="ICrossTenantAuditService.GetCrossTenantWebhookHealthAsync"/>.
/// </summary>
/// <param name="LagSeconds">
/// Wall-clock seconds between <see cref="LastCursorUpdate"/> and the
/// latest matching <c>audit.events.OccurredAt</c>. Null when no audit
/// events match the cursor's tenant or the cursor has never advanced.
/// </param>
public sealed record CrossTenantWebhookHealthRow(
    long TenantId,
    string TenantCode,
    string AdapterName,
    Guid LastProcessedEventId,
    DateTimeOffset LastCursorUpdate,
    double? LagSeconds);

/// <summary>
/// Per-tenant audit activity for the <see cref="TenantDetail"/> drill-down.
/// </summary>
public sealed record TenantAuditActivity(
    long TenantId,
    int TotalCount,
    IReadOnlyList<TenantAuditActivityRow> Rows);

/// <summary>
/// One row in the per-tenant audit activity table.
/// </summary>
public sealed record TenantAuditActivityRow(
    Guid EventId,
    string EventType,
    string EntityType,
    string EntityId,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? CorrelationId,
    bool IsError,
    string PayloadJson);

/// <summary>
/// Optional filter for <see cref="ICrossTenantAuditService.GetTenantAuditActivityAsync"/>.
/// </summary>
public sealed record TenantAuditActivityFilter(
    string? EventTypeContains = null,
    bool ErrorsOnly = false);

/// <summary>
/// Optional knobs on cross-tenant queries — primarily used by tests
/// to bypass the in-memory cache deterministically.
/// </summary>
public sealed record CrossTenantAuditQueryOptions(bool SkipCache = false);

/// <summary>
/// Configuration for <see cref="ICrossTenantAuditService"/>. Bound from
/// the <c>Portal:AuditDashboard</c> section.
/// </summary>
public sealed class CrossTenantAuditOptions
{
    /// <summary>Section name for binding from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.</summary>
    public const string SectionName = "Portal:AuditDashboard";

    /// <summary>Cache TTL for cross-tenant query results. Default 60 s.</summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum window the dashboard can ask for (clamped server-side).
    /// Default 30 days; the dashboard's window selector tops out at 30 d
    /// to keep the per-tick cost bounded.
    /// </summary>
    public TimeSpan MaxWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Default window for the dashboard's first render. 24 h.
    /// </summary>
    public TimeSpan DefaultWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Default <i>take</i> for <see cref="ICrossTenantAuditService.GetCrossTenantTopErrorsAsync"/>.
    /// Five fits cleanly in a card without scrolling; admins with bigger
    /// pilots can ask for more programmatically.
    /// </summary>
    public int TopErrorsTake { get; set; } = 5;
}
