using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 35 / B8.1 — application-side service backing the inbox UI on
/// <c>/notifications</c>. Consolidates the read + mark-read paths that
/// were previously duplicated between <c>NotificationsEndpoints</c> and
/// the original <c>Notifications.razor</c> page; both now route through
/// a single shape.
///
/// <para>
/// Reads against the <c>audit.notifications</c> table populated by the
/// Sprint 8 P3 <see cref="AuditNotificationProjector"/>. Tenant-isolation
/// + per-user-isolation are enforced at the DB layer by the
/// <c>tenant_user_isolation_notifications</c> RLS policy (Sprint 9 /
/// FU-userid); this service still threads the user id through to the
/// LINQ query as a defence-in-depth guard so a regression in RLS would
/// surface loudly rather than silently leak rows across users.
/// </para>
///
/// <para>
/// Mark-read writes emit a <c>nickerp.notification.read</c> audit event
/// per row that flips. Best-effort: a failed audit write does not roll
/// back the read flip (the system-of-record write already landed).
/// </para>
/// </summary>
public sealed class NotificationInboxService
{
    /// <summary>Default page size for <see cref="ListAsync"/>. Mirrors the v1 inbox.</summary>
    public const int DefaultPageSize = 20;

    private readonly AuditDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ILogger<NotificationInboxService> _logger;

    public NotificationInboxService(
        AuditDbContext db,
        TimeProvider clock,
        ITenantContext tenant,
        IEventPublisher events,
        ILogger<NotificationInboxService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Page the recipient's notifications newest-first. Filter narrows
    /// by read-state, event type, and a date range; null fields on
    /// <see cref="NotificationInboxFilter"/> mean "don't filter".
    /// </summary>
    public async Task<NotificationInboxPage> ListAsync(
        Guid userId,
        NotificationInboxFilter filter,
        int take = DefaultPageSize,
        int skip = 0,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty) throw new ArgumentException("userId is empty", nameof(userId));
        if (take <= 0) take = DefaultPageSize;
        if (take > 200) take = 200;
        if (skip < 0) skip = 0;
        filter ??= NotificationInboxFilter.All;

        var q = _db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId);

        switch (filter.ReadState)
        {
            case NotificationReadState.UnreadOnly:
                q = q.Where(n => n.ReadAt == null);
                break;
            case NotificationReadState.ReadOnly:
                q = q.Where(n => n.ReadAt != null);
                break;
            case NotificationReadState.All:
            default:
                break;
        }

        if (!string.IsNullOrWhiteSpace(filter.EventType))
        {
            var et = filter.EventType.Trim();
            q = q.Where(n => n.EventType == et);
        }

        if (filter.From is { } from)
        {
            q = q.Where(n => n.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            q = q.Where(n => n.CreatedAt <= to);
        }

        var totalCount = await q.CountAsync(ct);

        var unreadCount = await _db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .CountAsync(ct);

        var rows = await q
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(take)
            .Select(n => new NotificationInboxRow(
                n.Id,
                n.EventType,
                n.Title,
                n.Body,
                n.Link,
                n.CreatedAt,
                n.ReadAt))
            .ToListAsync(ct);

        return new NotificationInboxPage(
            Items: rows,
            TotalCount: totalCount,
            UnreadCount: unreadCount,
            Skip: skip,
            Take: take);
    }

    /// <summary>
    /// Distinct event types that have produced rows for this recipient.
    /// Used by the inbox filter dropdown so the operator can pick from
    /// the actual set of types they have rather than a free-text input.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListEventTypesAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return Array.Empty<string>();

        return await _db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .Select(n => n.EventType)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Idempotent: marking a row read that is already read is a no-op
    /// and does not emit a duplicate audit event. Returns
    /// <see langword="true"/> if the row flipped from unread to read on
    /// this call, <see langword="false"/> if it was already read or did
    /// not exist for this user.
    /// </summary>
    public async Task<bool> MarkReadAsync(
        Guid notificationId,
        Guid userId,
        CancellationToken ct = default)
    {
        if (notificationId == Guid.Empty) return false;
        if (userId == Guid.Empty) return false;

        var row = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (row is null || row.ReadAt is not null) return false;

        var now = _clock.GetUtcNow();
        row.ReadAt = now;
        await _db.SaveChangesAsync(ct);

        await EmitReadEventAsync(row, now, ct);
        return true;
    }

    /// <summary>
    /// Mark every unread notification for this recipient as read in one
    /// shot. Returns the count that flipped. Each flipped row emits a
    /// separate audit event so the trail is symmetric with single-row
    /// flips.
    /// </summary>
    public async Task<int> MarkAllReadAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return 0;

        var unread = await _db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ToListAsync(ct);
        if (unread.Count == 0) return 0;

        var now = _clock.GetUtcNow();
        foreach (var row in unread)
        {
            row.ReadAt = now;
        }
        await _db.SaveChangesAsync(ct);

        foreach (var row in unread)
        {
            await EmitReadEventAsync(row, now, ct);
        }
        return unread.Count;
    }

    /// <summary>
    /// One indexed COUNT for the bell + the inbox header. Cheap because
    /// the partial index <c>ix_notifications_user_unread</c> only
    /// stores rows where <c>ReadAt IS NULL</c>.
    /// </summary>
    public async Task<int> UnreadCountAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return 0;

        return await _db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .CountAsync(ct);
    }

    private async Task EmitReadEventAsync(Notification row, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var tenantId = _tenant.IsResolved ? _tenant.TenantId : row.TenantId;
            var payload = JsonSerializer.SerializeToElement(new
            {
                notificationId = row.Id,
                userId = row.UserId,
                eventType = row.EventType,
                originatingEventId = row.EventId,
                tenantId,
                readAt = now,
            });
            var key = IdempotencyKey.ForEntityChange(
                tenantId,
                "nickerp.notification.read",
                "Notification",
                row.Id.ToString(),
                now);
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: row.UserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: "nickerp.notification.read",
                entityType: "Notification",
                entityId: row.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit nickerp.notification.read for notification {NotificationId}",
                row.Id);
        }
    }
}

/// <summary>Read-state filter for <see cref="NotificationInboxService.ListAsync"/>.</summary>
public enum NotificationReadState
{
    /// <summary>No read-state filter applied.</summary>
    All = 0,
    /// <summary>Only rows where <see cref="Notification.ReadAt"/> is null.</summary>
    UnreadOnly = 1,
    /// <summary>Only rows where <see cref="Notification.ReadAt"/> is non-null.</summary>
    ReadOnly = 2,
}

/// <summary>
/// Filter parameters for <see cref="NotificationInboxService.ListAsync"/>.
/// Fields default to "no filter" so the typical "show everything" call
/// passes <see cref="All"/>.
/// </summary>
public sealed record NotificationInboxFilter(
    NotificationReadState ReadState = NotificationReadState.All,
    string? EventType = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null)
{
    /// <summary>No filter applied (paged "show everything").</summary>
    public static readonly NotificationInboxFilter All = new();
}

/// <summary>One inbox row exposed to the page / API.</summary>
public sealed record NotificationInboxRow(
    Guid Id,
    string EventType,
    string Title,
    string? Body,
    string? Link,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>Paged result of <see cref="NotificationInboxService.ListAsync"/>.</summary>
public sealed record NotificationInboxPage(
    IReadOnlyList<NotificationInboxRow> Items,
    int TotalCount,
    int UnreadCount,
    int Skip,
    int Take);
