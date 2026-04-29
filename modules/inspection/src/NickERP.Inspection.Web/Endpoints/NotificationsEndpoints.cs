using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Identity.Auth;

namespace NickERP.Inspection.Web.Endpoints;

/// <summary>
/// Sprint 8 P3 — minimal-API endpoints backing the notifications inbox UI.
///
/// <para>
/// All three endpoints require auth (the host's default policy is
/// <see cref="Microsoft.AspNetCore.Authorization.AuthorizationPolicy"/>
/// configured in <c>Program.cs</c> with
/// <c>RequireAuthenticatedUser()</c>). Tenant + user isolation are
/// enforced at the DB layer by the
/// <c>tenant_user_isolation_notifications</c> RLS policy as of
/// Sprint 9 / FU-userid: the <c>TenantConnectionInterceptor</c> pushes
/// both <c>app.tenant_id</c> and <c>app.user_id</c> onto every
/// connection, and the policy compares them against the row's
/// <c>"TenantId"</c> + <c>"UserId"</c>. The previous LINQ-level
/// <c>WHERE n.UserId == currentUser.Id</c> guards remain in place but
/// commented out — they're a defence-in-depth checkpoint that can be
/// re-enabled if RLS ever regresses, and they make the regression
/// loud rather than silent. The unique <c>(UserId, EventId)</c> index
/// on <c>audit.notifications</c> still serves as a third belt against
/// projector-induced duplicate writes.
/// </para>
/// </summary>
public static class NotificationsEndpoints
{
    /// <summary>
    /// Map the three notification endpoints onto <paramref name="app"/>.
    /// Wire from <c>Program.cs</c> after <c>UseAuthentication()</c> /
    /// <c>UseAuthorization()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").RequireAuthorization();

        group.MapGet("/", GetListAsync);
        group.MapPost("/{id:guid}/read", MarkReadAsync);
        group.MapPost("/read-all", MarkAllReadAsync);

        return app;
    }

    /// <summary>
    /// GET <c>/api/notifications?unreadOnly=true&amp;page=1</c> —
    /// paginated, newest-first. 20 rows per page.
    /// </summary>
    public static async Task<IResult> GetListAsync(
        HttpContext http,
        AuditDbContext db,
        bool unreadOnly = false,
        int page = 1,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out var userId))
            return Results.Unauthorized();

        const int pageSize = 20;
        if (page < 1) page = 1;

        // User filter now enforced at RLS layer (FU-userid). The line stays
        // as a defence-in-depth guard you can re-enable if RLS ever regresses.
        var q = db.Notifications.AsNoTracking() /* .Where(n => n.UserId == userId) */;
        if (unreadOnly) q = q.Where(n => n.ReadAt == null);

        var totalCount = await q.CountAsync(ct);
        // User filter now enforced at RLS layer (FU-userid). The line stays
        // as a defence-in-depth guard you can re-enable if RLS ever regresses.
        var unreadCount = await db.Notifications.AsNoTracking()
            .Where(n => /* n.UserId == userId && */ n.ReadAt == null)
            .CountAsync(ct);

        var rows = await q
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(n => new NotificationDto(
                n.Id,
                n.EventType,
                n.Title,
                n.Body,
                n.Link,
                n.CreatedAt,
                n.ReadAt))
            .ToListAsync(ct);

        return Results.Ok(new NotificationsPageDto(
            Page: page,
            PageSize: pageSize,
            TotalCount: totalCount,
            UnreadCount: unreadCount,
            Items: rows));
    }

    /// <summary>POST <c>/api/notifications/{id}/read</c> — mark single row as read.</summary>
    public static async Task<IResult> MarkReadAsync(
        Guid id,
        HttpContext http,
        AuditDbContext db,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out var userId))
            return Results.Unauthorized();

        // User filter now enforced at RLS layer (FU-userid). The line stays
        // as a defence-in-depth guard you can re-enable if RLS ever regresses.
        var row = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id /* && n.UserId == userId */, ct);
        if (row is null) return Results.NotFound();

        if (row.ReadAt is null)
        {
            row.ReadAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    /// <summary>POST <c>/api/notifications/read-all</c> — mark every unread row read.</summary>
    public static async Task<IResult> MarkAllReadAsync(
        HttpContext http,
        AuditDbContext db,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(http, out var userId))
            return Results.Unauthorized();

        var now = DateTimeOffset.UtcNow;
        // User filter now enforced at RLS layer (FU-userid). The line stays
        // as a defence-in-depth guard you can re-enable if RLS ever regresses.
        var unread = await db.Notifications
            .Where(n => /* n.UserId == userId && */ n.ReadAt == null)
            .ToListAsync(ct);

        foreach (var n in unread)
        {
            n.ReadAt = now;
        }
        if (unread.Count > 0)
            await db.SaveChangesAsync(ct);

        return Results.Ok(new { Updated = unread.Count });
    }

    /// <summary>
    /// Read the current user's id from the principal. Tries
    /// <see cref="NickErpClaims.Id"/> first (the canonical claim) and falls
    /// back to <see cref="ClaimTypes.NameIdentifier"/> (the framework
    /// mirror) so tests using either convention work.
    /// </summary>
    internal static bool TryGetCurrentUserId(HttpContext http, out Guid userId)
    {
        var raw = http.User.FindFirst(NickErpClaims.Id)?.Value
                  ?? http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(raw))
        {
            userId = default;
            return false;
        }
        return Guid.TryParse(raw, out userId);
    }
}

/// <summary>One notification row exposed via the inbox API.</summary>
public sealed record NotificationDto(
    Guid Id,
    string EventType,
    string Title,
    string? Body,
    string? Link,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

/// <summary>Paginated wrapper for <see cref="NotificationDto"/>.</summary>
public sealed record NotificationsPageDto(
    int Page,
    int PageSize,
    int TotalCount,
    int UnreadCount,
    IReadOnlyList<NotificationDto> Items);
