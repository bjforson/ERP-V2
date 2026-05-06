using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Portal.Services;

/// <summary>
/// Sprint 56 / FU-cross-tenant-aggregation — default
/// <see cref="ICrossTenantAuditService"/>.
///
/// <para>
/// Per-tenant fan-out (Sprint 36 / Sprint 44 pattern). Discovery hits the
/// <see cref="TenancyDbContext.Tenants"/> root (not under RLS), filters
/// to <see cref="TenantState.Active"/>, then for each tenant the service
/// opens a fresh DI scope, flips the <see cref="ITenantContext"/> via
/// <see cref="ITenantContext.SetTenant"/>, and queries
/// <see cref="AuditDbContext.Events"/> with an explicit
/// <c>e.TenantId == tenantId</c> filter. Per-tenant RLS narrows the
/// physical read; the LINQ filter is defence-in-depth + keeps the query
/// plan tight (matches <c>AuditNotificationProjector</c> posture).
/// </para>
///
/// <para>
/// <b>Caching.</b> All cross-tenant query results land in a process-local
/// <see cref="CrossTenantAuditCache"/> entry keyed by
/// (method, window-bucket). Cache TTL is configurable via
/// <see cref="CrossTenantAuditOptions.CacheTtl"/> (default 60 s). The TTL
/// is the single biggest knob keeping per-tick cost bounded as tenant
/// count grows; a 30 s value is reasonable for early pilots, a longer
/// value (5 min) for a settled multi-tenant deployment.
/// </para>
///
/// <para>
/// <b>Partition pruning.</b> Sprint 52 partitioned <c>audit.events</c>
/// by <c>OccurredAt</c>. Every query the implementation issues includes
/// an explicit <c>OccurredAt &gt;= window-from</c> filter so Postgres
/// can prune partitions and stay off the historical heap.
/// </para>
/// </summary>
public sealed class CrossTenantAuditService : ICrossTenantAuditService
{
    private readonly IServiceProvider _services;
    private readonly CrossTenantAuditOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<CrossTenantAuditService> _logger;
    private readonly CrossTenantAuditCache _cache;

    public CrossTenantAuditService(
        IServiceProvider services,
        IOptions<CrossTenantAuditOptions> options,
        ILogger<CrossTenantAuditService> logger,
        TimeProvider? clock = null,
        CrossTenantAuditCache? cache = null)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _cache = cache ?? new CrossTenantAuditCache(_clock);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrossTenantAuditSummaryRow>> GetCrossTenantSummaryAsync(
        TimeSpan window,
        CancellationToken ct = default)
    {
        var clamped = ClampWindow(window);
        var key = $"summary:{clamped.Ticks}";
        if (_cache.TryGet<IReadOnlyList<CrossTenantAuditSummaryRow>>(key, out var cached))
        {
            return cached!;
        }

        var from = _clock.GetUtcNow().Subtract(clamped);
        var tenants = await DiscoverActiveTenantsAsync(ct).ConfigureAwait(false);
        var result = new List<CrossTenantAuditSummaryRow>(tenants.Count);

        foreach (var t in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (eventCount, errorCount, lastEventAt) = await QueryTenantSummaryAsync(t.Id, from, ct).ConfigureAwait(false);
                result.Add(new CrossTenantAuditSummaryRow(
                    TenantId: t.Id,
                    TenantCode: t.Code,
                    TenantName: t.Name,
                    EventCount: eventCount,
                    ErrorCount: errorCount,
                    LastEventAt: lastEventAt));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One tenant failing must not crash the dashboard. Log + emit
                // a placeholder row so the operator sees the gap.
                _logger.LogWarning(ex,
                    "CrossTenantAuditService.GetCrossTenantSummaryAsync failed for tenant={TenantId}; emitting empty row.",
                    t.Id);
                result.Add(new CrossTenantAuditSummaryRow(
                    TenantId: t.Id,
                    TenantCode: t.Code,
                    TenantName: t.Name,
                    EventCount: 0,
                    ErrorCount: 0,
                    LastEventAt: null));
            }
        }

        IReadOnlyList<CrossTenantAuditSummaryRow> typed = result;
        _cache.Set(key, typed, _options.CacheTtl);
        return typed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrossTenantAuditTopErrorRow>> GetCrossTenantTopErrorsAsync(
        TimeSpan window,
        int take,
        CancellationToken ct = default)
    {
        var clampedWindow = ClampWindow(window);
        var clampedTake = Math.Clamp(take, 1, 50);
        var key = $"top-errors:{clampedWindow.Ticks}:{clampedTake}";
        if (_cache.TryGet<IReadOnlyList<CrossTenantAuditTopErrorRow>>(key, out var cached))
        {
            return cached!;
        }

        // Top-errors is a projection on top of the summary card; reuse the
        // summary (which is itself cached) so we don't fan out twice.
        var summary = await GetCrossTenantSummaryAsync(clampedWindow, ct).ConfigureAwait(false);
        var rows = summary
            .Where(r => r.EventCount > 0)
            .Select(r => new CrossTenantAuditTopErrorRow(
                TenantId: r.TenantId,
                TenantCode: r.TenantCode,
                TenantName: r.TenantName,
                ErrorCount: r.ErrorCount,
                EventCount: r.EventCount,
                ErrorRate: r.EventCount == 0 ? 0d : (double)r.ErrorCount / r.EventCount))
            .OrderByDescending(r => r.ErrorRate)
            .ThenByDescending(r => r.ErrorCount)
            .ThenBy(r => r.TenantCode, StringComparer.Ordinal)
            .Take(clampedTake)
            .ToList();

        IReadOnlyList<CrossTenantAuditTopErrorRow> typed = rows;
        _cache.Set(key, typed, _options.CacheTtl);
        return typed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrossTenantWebhookHealthRow>> GetCrossTenantWebhookHealthAsync(
        CancellationToken ct = default)
    {
        const string key = "webhook-health";
        if (_cache.TryGet<IReadOnlyList<CrossTenantWebhookHealthRow>>(key, out var cached))
        {
            return cached!;
        }

        var tenants = await DiscoverActiveTenantsAsync(ct).ConfigureAwait(false);
        if (tenants.Count == 0)
        {
            IReadOnlyList<CrossTenantWebhookHealthRow> emptyResult = Array.Empty<CrossTenantWebhookHealthRow>();
            _cache.Set(key, emptyResult, _options.CacheTtl);
            return emptyResult;
        }

        var rows = new List<CrossTenantWebhookHealthRow>();
        foreach (var t in tenants)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tenantRows = await QueryTenantWebhookHealthAsync(t, ct).ConfigureAwait(false);
                rows.AddRange(tenantRows);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Webhook cursor health is informational. If a tenant's
                // inspection DB is unreachable (or the table doesn't exist
                // because the inspection module isn't deployed on this
                // host), don't crash the dashboard.
                _logger.LogDebug(ex,
                    "CrossTenantAuditService.GetCrossTenantWebhookHealthAsync skipped tenant={TenantId} due to {ExceptionType}.",
                    t.Id, ex.GetType().Name);
            }
        }

        IReadOnlyList<CrossTenantWebhookHealthRow> typed = rows;
        _cache.Set(key, typed, _options.CacheTtl);
        return typed;
    }

    /// <inheritdoc />
    public async Task<TenantAuditActivity> GetTenantAuditActivityAsync(
        long tenantId,
        TimeSpan window,
        int take,
        TenantAuditActivityFilter? filter = null,
        CancellationToken ct = default)
    {
        if (tenantId <= 0)
        {
            return new TenantAuditActivity(tenantId, 0, Array.Empty<TenantAuditActivityRow>());
        }
        var clampedWindow = ClampWindow(window);
        var clampedTake = Math.Clamp(take, 1, 200);
        filter ??= new TenantAuditActivityFilter();

        // Per-tenant drill-down rows are inherently per-tenant, so cache
        // by (tenantId, window, take, filter) — but only for short bursts;
        // the dashboard's drill-down is interactive.
        var filterKey = $"{filter.EventTypeContains ?? string.Empty}|{filter.ErrorsOnly}";
        var key = $"tenant-activity:{tenantId}:{clampedWindow.Ticks}:{clampedTake}:{filterKey}";
        if (_cache.TryGet<TenantAuditActivity>(key, out var cached))
        {
            return cached!;
        }

        var from = _clock.GetUtcNow().Subtract(clampedWindow);

        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var audit = sp.GetRequiredService<AuditDbContext>();
        tenant.SetTenant(tenantId);
        await ResetConnectionAsync(audit, ct).ConfigureAwait(false);

        // OccurredAt filter first so partition pruning kicks in.
        var q = audit.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.OccurredAt >= from);

        if (!string.IsNullOrWhiteSpace(filter.EventTypeContains))
        {
            var needle = filter.EventTypeContains;
            q = q.Where(e => e.EventType.Contains(needle));
        }
        if (filter.ErrorsOnly)
        {
            q = q.Where(e => e.EventType.Contains(".error"));
        }

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        var rows = await q
            .OrderByDescending(e => e.OccurredAt)
            .Take(clampedTake)
            .Select(e => new
            {
                e.EventId,
                e.EventType,
                e.EntityType,
                e.EntityId,
                e.OccurredAt,
                e.ActorUserId,
                e.CorrelationId,
                Payload = e.Payload.RootElement.GetRawText()
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var mapped = rows
            .Select(r => new TenantAuditActivityRow(
                EventId: r.EventId,
                EventType: r.EventType,
                EntityType: r.EntityType,
                EntityId: r.EntityId,
                OccurredAt: r.OccurredAt,
                ActorUserId: r.ActorUserId,
                CorrelationId: r.CorrelationId,
                IsError: r.EventType.Contains(".error", StringComparison.Ordinal),
                PayloadJson: r.Payload))
            .ToList();

        var result = new TenantAuditActivity(tenantId, total, mapped);
        _cache.Set(key, result, _options.CacheTtl);
        return result;
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private TimeSpan ClampWindow(TimeSpan requested)
    {
        var floor = TimeSpan.FromHours(1);
        var ceiling = _options.MaxWindow > floor ? _options.MaxWindow : TimeSpan.FromDays(30);
        if (requested <= TimeSpan.Zero) return _options.DefaultWindow > floor ? _options.DefaultWindow : floor;
        if (requested < floor) return floor;
        if (requested > ceiling) return ceiling;
        return requested;
    }

    /// <summary>
    /// Tenant discovery via the <see cref="TenancyDbContext"/> root.
    /// Excludes <see cref="TenantState.SoftDeleted"/> and
    /// <see cref="TenantState.PendingHardPurge"/>; suspended tenants
    /// are also excluded (they have no live activity to surface). Pattern
    /// matches <c>SlaStateRefresherWorker.DiscoverActiveTenantsAsync</c>.
    /// </summary>
    private async Task<IReadOnlyList<TenantSlim>> DiscoverActiveTenantsAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .OrderBy(t => t.Code)
            .Select(t => new TenantSlim(t.Id, t.Code, t.Name))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    private async Task<(int Events, int Errors, DateTimeOffset? Last)> QueryTenantSummaryAsync(
        long tenantId,
        DateTimeOffset from,
        CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var audit = sp.GetRequiredService<AuditDbContext>();
        tenant.SetTenant(tenantId);
        await ResetConnectionAsync(audit, ct).ConfigureAwait(false);

        // Three-shot query — total + errors + max OccurredAt — rather than a
        // single GroupBy because the in-memory provider's GroupBy
        // translation is brittle (see ReportsService for the same pattern).
        var total = await audit.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.OccurredAt >= from)
            .CountAsync(ct)
            .ConfigureAwait(false);

        var errors = await audit.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.OccurredAt >= from && e.EventType.Contains(".error"))
            .CountAsync(ct)
            .ConfigureAwait(false);

        DateTimeOffset? last = null;
        if (total > 0)
        {
            // Project to a tuple of materialisable types; the
            // in-memory provider can't take Max on DateTimeOffset?
            // directly without OrderBy + Take(1). Same shape works
            // on Postgres.
            last = await audit.Events
                .AsNoTracking()
                .Where(e => e.TenantId == tenantId && e.OccurredAt >= from)
                .OrderByDescending(e => e.OccurredAt)
                .Select(e => (DateTimeOffset?)e.OccurredAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return (total, errors, last);
    }

    private async Task<IReadOnlyList<CrossTenantWebhookHealthRow>> QueryTenantWebhookHealthAsync(
        TenantSlim t,
        CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        tenant.SetTenant(t.Id);

        // InspectionDbContext is optional on this host (Program.cs only
        // wires it when ConnectionStrings:Inspection is set). Resolve
        // lazily so a portal-only deployment doesn't fan out into a
        // null-DbContext exception.
        var inspection = sp.GetService<InspectionDbContext>();
        if (inspection is null)
        {
            return Array.Empty<CrossTenantWebhookHealthRow>();
        }
        await ResetConnectionAsync(inspection, ct).ConfigureAwait(false);

        var cursors = await inspection.Set<WebhookCursor>()
            .AsNoTracking()
            .Where(c => c.TenantId == t.Id)
            .Select(c => new
            {
                c.AdapterName,
                c.LastProcessedEventId,
                c.UpdatedAt
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (cursors.Count == 0)
        {
            return Array.Empty<CrossTenantWebhookHealthRow>();
        }

        // Lag = (latest audit event for this tenant) - cursor.UpdatedAt.
        // We compute the per-tenant "latest audit event" once and reuse it
        // for every adapter.
        var audit = sp.GetRequiredService<AuditDbContext>();
        await ResetConnectionAsync(audit, ct).ConfigureAwait(false);
        var latest = await audit.Events
            .AsNoTracking()
            .Where(e => e.TenantId == t.Id)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => (DateTimeOffset?)e.OccurredAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return cursors.Select(c =>
        {
            double? lag = null;
            if (latest is not null)
            {
                var diff = (latest.Value - c.UpdatedAt).TotalSeconds;
                lag = diff > 0 ? diff : 0d;
            }
            return new CrossTenantWebhookHealthRow(
                TenantId: t.Id,
                TenantCode: t.Code,
                AdapterName: c.AdapterName,
                LastProcessedEventId: c.LastProcessedEventId,
                LastCursorUpdate: c.UpdatedAt,
                LagSeconds: lag);
        }).ToList();
    }

    /// <summary>
    /// Force a fresh connection on the DbContext so the
    /// <c>TenantConnectionInterceptor</c> re-pushes <c>app.tenant_id</c>
    /// with the new value. Same posture as
    /// <see cref="NickERP.Inspection.Web.Services.SlaStateRefresherWorker"/>.
    /// Best-effort: in-memory provider has no connection state to reset.
    /// </summary>
    private static async Task ResetConnectionAsync(DbContext db, CancellationToken ct)
    {
        try
        {
            if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // EF in-memory throws InvalidOperationException on
            // GetDbConnection; treat as no-op.
        }
        // Touch ct so the static method honours cancellation budgets if
        // we add async work above.
        if (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed record TenantSlim(long Id, string Code, string Name);
}

/// <summary>
/// Sprint 56 — process-local TTL cache for cross-tenant aggregator
/// results. Plain <see cref="Dictionary{TKey, TValue}"/> + a lock; query
/// fan-out is the expensive call, so contention on the cache itself is
/// not a concern. <see cref="IMemoryCache"/> is overkill for a single
/// service with &lt;10 keys.
/// </summary>
public sealed class CrossTenantAuditCache
{
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    public CrossTenantAuditCache(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public bool TryGet<T>(string key, out T? value) where T : class
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAt > _clock.GetUtcNow())
            {
                value = entry.Value as T;
                return value is not null;
            }
        }
        value = default;
        return false;
    }

    public void Set<T>(string key, T value, TimeSpan ttl) where T : class
    {
        var expiresAt = _clock.GetUtcNow().Add(ttl);
        lock (_gate)
        {
            _entries[key] = new CacheEntry(value, expiresAt);
        }
    }

    /// <summary>Clear the cache — exposed for tests and the dashboard's "Refresh now" button.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private sealed record CacheEntry(object Value, DateTimeOffset ExpiresAt);
}
