using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 33 / B7.1 — read-only aggregator for the
/// <c>/admin/reports</c> dashboard and its per-card drill-down pages.
///
/// <para>
/// Composes throughput / SLA / errors / audit summary cards from the
/// existing inspection + audit DbContexts. Vendor-neutral by design:
/// queries operate on shape (state machine, audit events) rather than
/// any port code, regime code, or vendor-specific column. v1's
/// "Reports & Analytics" landing page in NickScanWebApp.New surfaces a
/// fixed list of canned reports; this v2 take re-frames that as live
/// summary cards keyed on the v2 case + audit shape.
/// </para>
///
/// <para>
/// Tenant scope: the inspection DbContext is RLS-narrowed to
/// <see cref="ITenantContext.TenantId"/>; the audit DbContext joins
/// inherit the same RLS narrowing. Platform-admin "all tenants"
/// scope is intentionally NOT exposed here — Sprint 33 stays inside
/// the per-tenant DbContext to avoid widening the trust surface.
/// (FU: cross-tenant aggregation in the platform Portal — outside
/// the inspection module's blast radius.)
/// </para>
///
/// <para>
/// SLA card defensiveness: <c>inspection.sla_window</c> is being
/// added by Sprint 31 in parallel; B7 must tolerate the table being
/// absent at run-time. <see cref="GetSlaSummaryAsync"/> probes
/// <see cref="InspectionDbContext"/> for an <c>SlaWindows</c> DbSet
/// reflectively and falls back to a "tracking not enabled" placeholder
/// when the entity isn't mapped. Wraps the actual query in a
/// try/catch on <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>
/// + Postgres "relation does not exist" so a stale schema in the dev
/// box also degrades gracefully.
/// </para>
/// </summary>
public class ReportsService
{
    private readonly InspectionDbContext _db;
    private readonly AuditDbContext _audit;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReportsService> _logger;

    public ReportsService(
        InspectionDbContext db,
        AuditDbContext audit,
        ITenantContext tenant,
        TimeProvider clock,
        ILogger<ReportsService> logger)
    {
        _db = db;
        _audit = audit;
        _tenant = tenant;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// One-shot dashboard composition. Pulls all four cards in
    /// parallel-ish via four separate awaits — the underlying DbContext
    /// is single-threaded, so we don't gain by Task.WhenAll'ing
    /// queries on the same DbContext (would throw under EF).
    /// </summary>
    public async Task<ReportsDashboardSummary> GetDashboardSummaryAsync(CancellationToken ct = default)
    {
        EnsureTenantResolved();

        var throughput = await GetThroughputSummaryAsync(ct).ConfigureAwait(false);
        var sla = await GetSlaSummaryAsync(ct).ConfigureAwait(false);
        var errors = await GetErrorsSummaryAsync(ct).ConfigureAwait(false);
        var audit = await GetAuditEventsSummaryAsync(ct).ConfigureAwait(false);

        return new ReportsDashboardSummary(
            Throughput: throughput,
            Sla: sla,
            Errors: errors,
            Audit: audit);
    }

    // -----------------------------------------------------------------
    // Throughput card
    // -----------------------------------------------------------------

    /// <summary>
    /// Counts cases created / decided / submitted in the trailing
    /// 24h, 7d, 30d windows. "Decided" = state machine has progressed
    /// past <see cref="InspectionWorkflowState.Open"/> /
    /// <see cref="InspectionWorkflowState.Claimed"/> (i.e. the analyst
    /// has emitted a decision). "Submitted" = the case has at least
    /// one OutboundSubmission row keyed on it within the window.
    /// </summary>
    public async Task<ThroughputSummary> GetThroughputSummaryAsync(CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var now = _clock.GetUtcNow();
        var w24h = now.AddHours(-24);
        var w7d = now.AddDays(-7);
        var w30d = now.AddDays(-30);

        // Cases created — bucket by OpenedAt.
        var created24h = await _db.Cases.AsNoTracking().CountAsync(c => c.OpenedAt >= w24h, ct).ConfigureAwait(false);
        var created7d = await _db.Cases.AsNoTracking().CountAsync(c => c.OpenedAt >= w7d, ct).ConfigureAwait(false);
        var created30d = await _db.Cases.AsNoTracking().CountAsync(c => c.OpenedAt >= w30d, ct).ConfigureAwait(false);

        // Cases decided — StateEnteredAt within the window AND state is
        // Reviewed or beyond (analyst has produced findings). Vendor-
        // neutral; uses the v2 generic workflow states.
        var decided24h = await _db.Cases.AsNoTracking()
            .CountAsync(c => c.StateEnteredAt >= w24h
                && (int)c.State >= (int)InspectionWorkflowState.Reviewed
                && c.State != InspectionWorkflowState.Cancelled, ct)
            .ConfigureAwait(false);
        var decided7d = await _db.Cases.AsNoTracking()
            .CountAsync(c => c.StateEnteredAt >= w7d
                && (int)c.State >= (int)InspectionWorkflowState.Reviewed
                && c.State != InspectionWorkflowState.Cancelled, ct)
            .ConfigureAwait(false);
        var decided30d = await _db.Cases.AsNoTracking()
            .CountAsync(c => c.StateEnteredAt >= w30d
                && (int)c.State >= (int)InspectionWorkflowState.Reviewed
                && c.State != InspectionWorkflowState.Cancelled, ct)
            .ConfigureAwait(false);

        // Cases submitted — distinct CaseId on OutboundSubmissions
        // dispatched within the window. SubmittedAt is the row's birth
        // timestamp on this entity (no separate CreatedAt column).
        var submitted24h = await _db.OutboundSubmissions.AsNoTracking()
            .Where(s => s.SubmittedAt >= w24h)
            .Select(s => s.CaseId)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);
        var submitted7d = await _db.OutboundSubmissions.AsNoTracking()
            .Where(s => s.SubmittedAt >= w7d)
            .Select(s => s.CaseId)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);
        var submitted30d = await _db.OutboundSubmissions.AsNoTracking()
            .Where(s => s.SubmittedAt >= w30d)
            .Select(s => s.CaseId)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);

        return new ThroughputSummary(
            CreatedLast24h: created24h,
            CreatedLast7d: created7d,
            CreatedLast30d: created30d,
            DecidedLast24h: decided24h,
            DecidedLast7d: decided7d,
            DecidedLast30d: decided30d,
            SubmittedLast24h: submitted24h,
            SubmittedLast7d: submitted7d,
            SubmittedLast30d: submitted30d);
    }

    /// <summary>
    /// Per-day throughput breakdown over the trailing 30 days — feeds
    /// the throughput drill-down page's table + CSV export. Buckets by
    /// the case's <c>OpenedAt</c> in UTC (consumers can render local
    /// timezone client-side if they want).
    /// </summary>
    public async Task<IReadOnlyList<ThroughputDayBucket>> GetThroughputDailyAsync(
        int days = 30,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var now = _clock.GetUtcNow();
        var window = now.AddDays(-Math.Max(1, days));

        // EF in-memory + Npgsql both support DateOnly conversion, but
        // grouping by date_trunc('day', ...) is the canonical Postgres
        // path. We do the bucketing client-side after a project to a
        // narrow tuple to keep the query simple + provider-portable.
        var rows = await _db.Cases.AsNoTracking()
            .Where(c => c.OpenedAt >= window)
            .Select(c => new { c.OpenedAt, c.State, c.StateEnteredAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var buckets = rows
            .GroupBy(r => DateOnly.FromDateTime(r.OpenedAt.UtcDateTime))
            .Select(g => new ThroughputDayBucket(
                Day: g.Key,
                Created: g.Count(),
                Decided: g.Count(r =>
                    (int)r.State >= (int)InspectionWorkflowState.Reviewed &&
                    r.State != InspectionWorkflowState.Cancelled)))
            .OrderBy(b => b.Day)
            .ToList();

        return buckets;
    }

    // -----------------------------------------------------------------
    // SLA card (defensive vs. Sprint 31 race)
    // -----------------------------------------------------------------

    /// <summary>
    /// Surface SLA window stats if Sprint 31 has shipped the
    /// <c>inspection.sla_window</c> table; otherwise return a
    /// placeholder summary.
    ///
    /// <para>
    /// We probe by looking for an <see cref="IEntityType"/> named
    /// "SlaWindow" on <see cref="InspectionDbContext.Model"/>. If the
    /// entity is not registered, the table is not in this build —
    /// return <see cref="SlaSummary.NotEnabled"/>. If it is registered
    /// but the table doesn't exist at the DB level (developer skipped
    /// a migration), the catch handles a Npgsql relation-not-found.
    /// </para>
    /// </summary>
    public async Task<SlaSummary> GetSlaSummaryAsync(CancellationToken ct = default)
    {
        EnsureTenantResolved();

        // Probe the model — does an entity named SlaWindow exist?
        // Sprint 31 owns the Core/Database project; if they ship the
        // entity, it'll show up here without a code edit on our side.
        var entityType = _db.Model.FindEntityType("NickERP.Inspection.Core.Entities.SlaWindow")
            ?? _db.Model.GetEntityTypes()
                .FirstOrDefault(t => t.ClrType.Name == "SlaWindow");

        if (entityType is null)
        {
            return SlaSummary.NotEnabled;
        }

        // Try the live query. If the table is mapped but missing at
        // the DB level (migration not applied), Npgsql returns
        // "relation does not exist" — degrade to NotEnabled rather
        // than throwing into the page.
        try
        {
            var now = _clock.GetUtcNow();
            // Use a raw IQueryable for the entity type so we don't have
            // to take a project ref on Sprint 31's new DbSet at compile
            // time. EF's Set<T>(string) is the supported pattern but
            // requires the CLR type — fall back to a SQL probe via the
            // schema table as a portable second-best.
            //
            // For the in-memory provider tests we use the strongly-
            // typed DbSet path when available via reflection; for
            // production we rely on this code being recompiled once
            // Sprint 31 lands.
            var clrType = entityType.ClrType;
            var setMethod = typeof(DbContext).GetMethods()
                .FirstOrDefault(m => m.Name == "Set" && m.IsGenericMethod && m.GetParameters().Length == 0);
            if (setMethod is null) return SlaSummary.NotEnabled;
            var generic = setMethod.MakeGenericMethod(clrType);
            var dbSet = generic.Invoke(_db, Array.Empty<object>()) as IQueryable
                ?? null;
            if (dbSet is null) return SlaSummary.NotEnabled;

            // We can't statically project — so cast to IQueryable<object>
            // and count by deadline / state names via dynamic LINQ-via-
            // projections is overkill for B7. Instead we accept that
            // until Sprint 31 lands the strongly-typed reader, the
            // dashboard surfaces a "table present but reader not yet
            // wired" hint. Tests that supply a typed entity exercise
            // the typed path via TryGetTypedSlaSummary.
            var typed = await TryGetTypedSlaSummaryAsync(now, ct).ConfigureAwait(false);
            return typed ?? new SlaSummary(
                IsEnabled: true,
                OpenWindowCount: 0,
                BreachCount: 0,
                AtRiskCount: 0,
                Note: "SLA tracking is wired but the typed reader is pending Sprint 31 merge.");
        }
        catch (Exception ex) when (IsRelationDoesNotExist(ex))
        {
            _logger.LogInformation(ex,
                "ReportsService: inspection.sla_window mapped but missing at the DB level; degrading to NotEnabled");
            return SlaSummary.NotEnabled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReportsService: SLA summary query failed; surfacing NotEnabled placeholder so the dashboard stays usable");
            return SlaSummary.NotEnabled;
        }
    }

    /// <summary>
    /// Strongly-typed SLA reader hook. Sprint 31 has shipped the
    /// <see cref="SlaWindow"/> entity, so the base implementation now
    /// queries the typed DbSet directly. Kept <c>protected virtual</c>
    /// so future evolutions (e.g. snapshot-table read or projector view)
    /// can override without rewriting the public surface.
    /// </summary>
    protected virtual async Task<SlaSummary?> TryGetTypedSlaSummaryAsync(
        DateTimeOffset now,
        CancellationToken ct)
    {
        var query = _db.Set<SlaWindow>().AsNoTracking();
        var open = await query.Where(w => w.ClosedAt == null).CountAsync(ct).ConfigureAwait(false);
        var breached = await query.Where(w => w.State == SlaWindowState.Breached).CountAsync(ct).ConfigureAwait(false);
        var atRisk = await query.Where(w => w.State == SlaWindowState.AtRisk).CountAsync(ct).ConfigureAwait(false);
        return new SlaSummary(
            IsEnabled: true,
            OpenWindowCount: open,
            BreachCount: breached,
            AtRiskCount: atRisk,
            Note: null);
    }

    private static bool IsRelationDoesNotExist(Exception ex)
    {
        // Walk inner exceptions; Npgsql throws PostgresException with
        // SqlState 42P01 for missing tables. We don't take an Npgsql
        // ref so check by string. This is defensive code — when in
        // doubt, return false and let the outer catch log.
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            var name = e.GetType().Name;
            if (name == "PostgresException")
            {
                var sqlState = e.GetType().GetProperty("SqlState")?.GetValue(e) as string;
                if (sqlState == "42P01") return true;
            }
            if (e.Message.Contains("relation", StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // -----------------------------------------------------------------
    // Errors card
    // -----------------------------------------------------------------

    /// <summary>
    /// Counts audit events whose <c>EventType</c> ends with
    /// <c>.error</c> (or contains it) in the trailing 24h / 7d
    /// windows. Vendor-neutral: we don't constrain the entity type
    /// or payload schema — any module that emits a *.error audit
    /// event surfaces here.
    /// </summary>
    public async Task<ErrorsSummary> GetErrorsSummaryAsync(CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var now = _clock.GetUtcNow();
        var w24h = now.AddHours(-24);
        var w7d = now.AddDays(-7);

        var tenantId = _tenant.TenantId;

        var count24h = await _audit.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && e.OccurredAt >= w24h
                && e.EventType.Contains(".error"))
            .CountAsync(ct)
            .ConfigureAwait(false);
        var count7d = await _audit.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && e.OccurredAt >= w7d
                && e.EventType.Contains(".error"))
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Most-recent 5 entity types — aids the "what's broken?" pulse.
        // EF in-memory can't translate a typed-record projection over a
        // GroupBy result; project to anonymous first, materialise, then
        // map. The Postgres provider handles either path; this shape
        // stays portable.
        var recentTypesRaw = await _audit.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && e.OccurredAt >= w7d
                && e.EventType.Contains(".error"))
            .GroupBy(e => e.EventType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var recentTypes = recentTypesRaw
            .OrderByDescending(b => b.Count)
            .Take(5)
            .Select(b => new ErrorTypeBreakdown(b.Type, b.Count))
            .ToList();

        return new ErrorsSummary(
            CountLast24h: count24h,
            CountLast7d: count7d,
            TopTypesLast7d: recentTypes);
    }

    /// <summary>
    /// Paged + filtered list of error events for the
    /// <c>/admin/reports/errors</c> drill-down page. Optional event-
    /// type substring + entity-type substring + date range.
    /// </summary>
    public async Task<PagedResult<ErrorEventRow>> ListErrorEventsAsync(
        string? eventTypeFilter,
        string? entityTypeFilter,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var tenantId = _tenant.TenantId;

        var q = _audit.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType.Contains(".error"));

        if (!string.IsNullOrWhiteSpace(eventTypeFilter))
            q = q.Where(e => e.EventType.Contains(eventTypeFilter));
        if (!string.IsNullOrWhiteSpace(entityTypeFilter))
            q = q.Where(e => e.EntityType.Contains(entityTypeFilter));
        if (from.HasValue)
            q = q.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue)
            q = q.Where(e => e.OccurredAt <= to.Value);

        var total = await q.CountAsync(ct).ConfigureAwait(false);

        var rows = await q
            .OrderByDescending(e => e.OccurredAt)
            .Skip(Math.Max(0, page - 1) * Math.Max(1, pageSize))
            .Take(Math.Max(1, pageSize))
            .Select(e => new ErrorEventRow(
                e.EventId,
                e.EventType,
                e.EntityType,
                e.EntityId,
                e.OccurredAt,
                e.ActorUserId,
                e.CorrelationId,
                e.Payload.RootElement.GetRawText()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<ErrorEventRow>(
            Total: total,
            Page: Math.Max(1, page),
            PageSize: Math.Max(1, pageSize),
            Rows: rows);
    }

    // -----------------------------------------------------------------
    // Audit summary card
    // -----------------------------------------------------------------

    /// <summary>
    /// Top 10 audit-event types emitted by this tenant in the
    /// trailing 24h. Used by both the dashboard card and the
    /// drill-down page (which surfaces a wider time range + paged
    /// detail list).
    /// </summary>
    public async Task<AuditEventsSummary> GetAuditEventsSummaryAsync(CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var now = _clock.GetUtcNow();
        var w24h = now.AddHours(-24);
        var tenantId = _tenant.TenantId;

        var total = await _audit.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.OccurredAt >= w24h)
            .CountAsync(ct)
            .ConfigureAwait(false);

        // Materialise the GroupBy first, then map to the typed record.
        // Same shape as GetErrorsSummaryAsync — keeps the in-memory
        // provider happy without sacrificing the Postgres path.
        var topTypesRaw = await _audit.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.OccurredAt >= w24h)
            .GroupBy(e => e.EventType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var topTypes = topTypesRaw
            .OrderByDescending(b => b.Count)
            .Take(10)
            .Select(b => new EventTypeBucket(b.Type, b.Count))
            .ToList();

        return new AuditEventsSummary(
            TotalLast24h: total,
            TopTypesLast24h: topTypes);
    }

    /// <summary>
    /// Paged audit-event list for the <c>/admin/reports/audit</c>
    /// drill-down. Filters by EventType substring, actor user id,
    /// and time range.
    /// </summary>
    public async Task<PagedResult<AuditEventRow>> ListAuditEventsAsync(
        string? eventTypeFilter,
        Guid? actorUserId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var tenantId = _tenant.TenantId;

        var q = _audit.Events.AsNoTracking()
            .Where(e => e.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(eventTypeFilter))
            q = q.Where(e => e.EventType.Contains(eventTypeFilter));
        if (actorUserId.HasValue)
            q = q.Where(e => e.ActorUserId == actorUserId.Value);
        if (from.HasValue)
            q = q.Where(e => e.OccurredAt >= from.Value);
        if (to.HasValue)
            q = q.Where(e => e.OccurredAt <= to.Value);

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(e => e.OccurredAt)
            .Skip(Math.Max(0, page - 1) * Math.Max(1, pageSize))
            .Take(Math.Max(1, pageSize))
            .Select(e => new AuditEventRow(
                e.EventId,
                e.EventType,
                e.EntityType,
                e.EntityId,
                e.OccurredAt,
                e.ActorUserId,
                e.CorrelationId,
                e.Payload.RootElement.GetRawText()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<AuditEventRow>(
            Total: total,
            Page: Math.Max(1, page),
            PageSize: Math.Max(1, pageSize),
            Rows: rows);
    }

    // -----------------------------------------------------------------

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; ReportsService must run inside a tenant-aware request scope.");
    }
}

// =====================================================================
// DTOs
// =====================================================================

/// <summary>Composite dashboard summary.</summary>
public sealed record ReportsDashboardSummary(
    ThroughputSummary Throughput,
    SlaSummary Sla,
    ErrorsSummary Errors,
    AuditEventsSummary Audit);

/// <summary>Throughput card payload.</summary>
public sealed record ThroughputSummary(
    int CreatedLast24h, int CreatedLast7d, int CreatedLast30d,
    int DecidedLast24h, int DecidedLast7d, int DecidedLast30d,
    int SubmittedLast24h, int SubmittedLast7d, int SubmittedLast30d);

/// <summary>One row in the per-day throughput drill-down.</summary>
public sealed record ThroughputDayBucket(DateOnly Day, int Created, int Decided);

/// <summary>
/// SLA card payload. <see cref="IsEnabled"/> = false means Sprint 31
/// hasn't shipped the <c>inspection.sla_window</c> table yet, or the
/// table is mapped but missing at the DB level.
/// </summary>
public sealed record SlaSummary(
    bool IsEnabled,
    int OpenWindowCount,
    int BreachCount,
    int AtRiskCount,
    string? Note)
{
    /// <summary>Singleton placeholder for the "Sprint 31 hasn't shipped" path.</summary>
    public static SlaSummary NotEnabled { get; } = new(
        IsEnabled: false,
        OpenWindowCount: 0,
        BreachCount: 0,
        AtRiskCount: 0,
        Note: "SLA tracking is not yet enabled. Awaiting Sprint 31 (completeness + SLA dashboards) merge.");
}

/// <summary>Errors card payload.</summary>
public sealed record ErrorsSummary(
    int CountLast24h,
    int CountLast7d,
    IReadOnlyList<ErrorTypeBreakdown> TopTypesLast7d);

/// <summary>One row in the errors-by-type breakdown.</summary>
public sealed record ErrorTypeBreakdown(string EventType, int Count);

/// <summary>One row in the errors drill-down list.</summary>
public sealed record ErrorEventRow(
    Guid EventId,
    string EventType,
    string EntityType,
    string EntityId,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? CorrelationId,
    string PayloadJson);

/// <summary>Audit summary card payload.</summary>
public sealed record AuditEventsSummary(
    int TotalLast24h,
    IReadOnlyList<EventTypeBucket> TopTypesLast24h);

/// <summary>One bucket in the audit-events-by-type breakdown.</summary>
public sealed record EventTypeBucket(string EventType, int Count);

/// <summary>One row in the audit drill-down list.</summary>
public sealed record AuditEventRow(
    Guid EventId,
    string EventType,
    string EntityType,
    string EntityId,
    DateTimeOffset OccurredAt,
    Guid? ActorUserId,
    string? CorrelationId,
    string PayloadJson);

/// <summary>Generic paged result wrapper for the drill-down lists.</summary>
public sealed record PagedResult<T>(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<T> Rows);
