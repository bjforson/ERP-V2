using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Platform.Audit.Database.Services;

/// <summary>
/// Sprint 8 P3 — <see cref="BackgroundService"/> that polls
/// <c>audit.events</c> for new rows since the last checkpoint and
/// projects them into <c>audit.notifications</c> via the registered
/// <see cref="INotificationRule"/> set.
///
/// <para>
/// Mirrors the per-tenant scope discipline used by
/// <c>PreRenderWorker</c> in the inspection module: enumerate the set of
/// tenants that have new <c>audit.events</c> rows since the checkpoint,
/// then for each tenant project the events into notifications.
/// </para>
///
/// <para>
/// Sprint 9 / FU-userid — the per-tenant fan-out runs under
/// <see cref="ITenantContext.SetSystemContext"/> rather than
/// <see cref="ITenantContext.SetTenant"/>. Reason: the new
/// <c>tenant_user_isolation_notifications</c> RLS policy on
/// <c>audit.notifications</c> compares <c>"UserId"</c> against
/// <c>app.user_id</c>, and a background worker has no current user to
/// push (the interceptor pushes the zero UUID), so per-tenant <c>SetTenant</c>
/// + a real <c>UserId</c> on the row fails the WITH CHECK. The OR clause
/// on <c>app.tenant_id = '-1'</c> mirrors Sprint 5's <c>audit.events</c>
/// opt-in and admits these writes. Per-tenant fan-out is preserved
/// because the LINQ <c>where e.TenantId == tenantId</c> still narrows
/// reads — RLS is permissive under system context but the LINQ filter
/// keeps the query plan tight. Both the projector caller and the
/// <c>audit.notifications</c> table are registered in
/// <c>docs/system-context-audit-register.md</c>.
/// </para>
///
/// <para>
/// Polling cadence: 5s in production / 1s in development, configurable
/// via <see cref="AuditNotificationProjectorOptions"/>. The polling tax
/// is two indexed queries per active tenant per tick — cheap enough to
/// not bother with LISTEN/NOTIFY until volume warrants.
/// </para>
/// </summary>
public sealed class AuditNotificationProjector : BackgroundService
{
    /// <summary>Stable name written to <c>audit.projection_checkpoints.ProjectionName</c>.</summary>
    public const string ProjectorName = "AuditNotificationProjector";

    private readonly IServiceProvider _services;
    private readonly IOptions<AuditNotificationProjectorOptions> _opts;
    private readonly ILogger<AuditNotificationProjector> _logger;

    public AuditNotificationProjector(
        IServiceProvider services,
        IOptions<AuditNotificationProjectorOptions> opts,
        ILogger<AuditNotificationProjector> logger)
    {
        _services = services;
        _opts = opts;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(1, _opts.Value.PollIntervalSeconds));
        _logger.LogInformation(
            "AuditNotificationProjector started — polling every {Interval}s, batch {Batch}",
            poll.TotalSeconds, _opts.Value.BatchSize);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var produced = await ProjectOnceAsync(ct);
                if (produced > 0)
                    _logger.LogDebug(
                        "AuditNotificationProjector projected {Count} notification row(s) this cycle",
                        produced);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AuditNotificationProjector cycle failed; backing off");
            }

            try { await Task.Delay(poll, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// Run a single projection pass. Public + virtual-equivalent shape so
    /// tests can drive it deterministically without spinning the
    /// host. Returns the total notifications inserted across all tenants.
    /// </summary>
    public async Task<int> ProjectOnceAsync(CancellationToken ct)
    {
        // Bookmark resolution + checkpoint update happen under a SYSTEM-WIDE
        // (no tenant) DbContext because audit.projection_checkpoints is
        // intentionally not under RLS. Tenant fan-out happens below in a
        // per-tenant scope.
        DateTimeOffset checkpoint;
        await using (var ckScope = _services.CreateAsyncScope())
        {
            var db = ckScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            checkpoint = await ResolveCheckpointAsync(db, ct);
        }

        // Discover the set of (TenantId) groups that have new events
        // since the checkpoint. We can't query audit.events with RLS
        // before knowing the tenant — so we use the system context
        // (audit.events is the only opt-in table) to enumerate distinct
        // tenants with new rows. SetSystemContext is allowed here per
        // docs/system-context-audit-register.md (audit.events is the one
        // table opted in).
        IReadOnlyList<long> tenantsWithNewEvents;
        await using (var discoveryScope = _services.CreateAsyncScope())
        {
            var sp = discoveryScope.ServiceProvider;
            var db = sp.GetRequiredService<AuditDbContext>();
            var tenant = sp.GetRequiredService<ITenantContext>();
            tenant.SetSystemContext();

            // Force a fresh connection so the interceptor sees the new
            // system-context value when it runs SET app.tenant_id.
            try
            {
                if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                    await db.Database.CloseConnectionAsync();
            }
            catch { /* best-effort */ }

            tenantsWithNewEvents = await db.Events
                .AsNoTracking()
                .Where(e => e.IngestedAt > checkpoint && e.TenantId != null)
                .Select(e => e.TenantId!.Value)
                .Distinct()
                .ToListAsync(ct);
        }

        if (tenantsWithNewEvents.Count == 0)
        {
            // Even with no new events, advance the checkpoint to the
            // server-clock "now" so we don't keep scanning the same
            // historical range — but only if there were no skipped
            // tenants (always true here). Cheap and avoids skew.
            return 0;
        }

        int totalInserted = 0;
        DateTimeOffset newCheckpoint = checkpoint;

        foreach (var tenantId in tenantsWithNewEvents)
        {
            ct.ThrowIfCancellationRequested();

            var (inserted, maxIngested) = await ProjectTenantAsync(tenantId, checkpoint, ct);
            totalInserted += inserted;
            if (maxIngested > newCheckpoint) newCheckpoint = maxIngested;
        }

        // Advance the checkpoint after every tenant has been processed
        // for this tick. Crash mid-loop simply re-runs those tenants on
        // the next tick — the unique (UserId, EventId) index makes that
        // safe.
        if (newCheckpoint > checkpoint)
        {
            await using var saveScope = _services.CreateAsyncScope();
            var db = saveScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            await UpsertCheckpointAsync(db, newCheckpoint, ct);
        }

        return totalInserted;
    }

    private async Task<(int Inserted, DateTimeOffset MaxIngested)> ProjectTenantAsync(
        long tenantId,
        DateTimeOffset checkpoint,
        CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AuditDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();
        var rules = sp.GetServices<INotificationRule>().ToList();

        if (rules.Count == 0) return (0, checkpoint);

        // Sprint 9 / FU-userid — system context lets the projector pass
        // the tenant_user_isolation_notifications WITH CHECK clause when
        // it inserts rows whose UserId belongs to a real human (the
        // projector itself has no current user, so app.user_id is the
        // zero UUID and a non-system context would fail the policy).
        // audit.events is already opted in (Sprint 5), audit.notifications
        // is opted in by FU-userid; both are tracked in
        // docs/system-context-audit-register.md.
        tenant.SetSystemContext();
        // Force a fresh connection so the interceptor pushes the new
        // app.tenant_id = '-1' (pooled connection might still carry the
        // previous tenant id from another tick).
        try
        {
            if (db.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                await db.Database.CloseConnectionAsync();
        }
        catch { /* best-effort */ }

        var batchSize = Math.Max(1, _opts.Value.BatchSize);

        // Pull events under system context — RLS on audit.events admits
        // both per-tenant rows and NULL-tenant suite-wide rows under the
        // sentinel. We narrow to this tenant in LINQ for query-plan
        // ergonomics + correctness (we deliberately don't want suite-wide
        // events folded into a tenant's notification stream here). Filter
        // by EventType IN (rule.EventType) so we don't materialise events
        // no rule subscribes to.
        var subscribedTypes = rules.Select(r => r.EventType).Distinct().ToArray();

        var events = await db.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                && e.IngestedAt > checkpoint
                && subscribedTypes.Contains(e.EventType))
            .OrderBy(e => e.IngestedAt)
            .Take(batchSize)
            .ToListAsync(ct);

        if (events.Count == 0) return (0, checkpoint);

        DateTimeOffset maxIngested = checkpoint;
        int inserted = 0;

        foreach (var evt in events)
        {
            ct.ThrowIfCancellationRequested();
            if (evt.IngestedAt > maxIngested) maxIngested = evt.IngestedAt;

            // Match every rule whose EventType equals this row's. Multiple
            // rules can subscribe to the same type; each fan-out happens
            // independently.
            foreach (var rule in rules.Where(r => r.EventType == evt.EventType))
            {
                IReadOnlyList<Notification> notifications;
                try
                {
                    notifications = await rule.ProjectAsync(evt, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "Notification rule {Rule} threw on event {EventId}; skipping",
                        rule.GetType().Name, evt.EventId);
                    continue;
                }

                foreach (var n in notifications)
                {
                    if (n is null) continue;

                    // The TenantOwnedEntityInterceptor stamps TenantId on
                    // insert when the rule left it as 0; pre-set it for
                    // explicitness so RLS WITH CHECK passes deterministically.
                    if (n.TenantId == 0) n.TenantId = tenantId;

                    db.Notifications.Add(n);
                    try
                    {
                        await db.SaveChangesAsync(ct);
                        inserted++;
                    }
                    catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                    {
                        // Idempotent re-projection — another tick or a
                        // pre-crash partial save won this insert. Detach
                        // the failed entity so the next iteration's
                        // SaveChanges doesn't retry it.
                        var entry = db.Entry(n);
                        entry.State = EntityState.Detached;
                        _logger.LogDebug(
                            "Notification for event {EventId}/user {UserId} already exists; skipping benign duplicate",
                            evt.EventId, n.UserId);
                    }
                }
            }
        }

        return (inserted, maxIngested);
    }

    /// <summary>
    /// Read the current checkpoint row, defaulting to <see cref="DateTimeOffset.MinValue"/>
    /// on first run. Reads execute outside any tenant scope (the table
    /// is intentionally not under RLS).
    /// </summary>
    private static async Task<DateTimeOffset> ResolveCheckpointAsync(
        AuditDbContext db,
        CancellationToken ct)
    {
        var existing = await db.ProjectionCheckpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectionName == ProjectorName, ct);
        return existing?.LastIngestedAt ?? DateTimeOffset.MinValue;
    }

    private static async Task UpsertCheckpointAsync(
        AuditDbContext db,
        DateTimeOffset newValue,
        CancellationToken ct)
    {
        var existing = await db.ProjectionCheckpoints
            .FirstOrDefaultAsync(c => c.ProjectionName == ProjectorName, ct);
        if (existing is null)
        {
            db.ProjectionCheckpoints.Add(new ProjectionCheckpoint
            {
                ProjectionName = ProjectorName,
                LastIngestedAt = newValue,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.LastIngestedAt = newValue;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.GetType().Name.Contains("PostgresException", StringComparison.Ordinal) == true
           && ex.InnerException.Message.Contains("ux_notifications_user_event", StringComparison.Ordinal);
}

/// <summary>Configuration for <see cref="AuditNotificationProjector"/>.</summary>
public sealed class AuditNotificationProjectorOptions
{
    /// <summary>Polling interval between projection ticks. 5s prod / 1s dev by default.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Max events to materialise per tenant per tick.</summary>
    public int BatchSize { get; set; } = 100;
}
