using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Audit.Database.Entities;

/// <summary>
/// User-facing notification row, written by
/// <see cref="Services.AuditNotificationProjector"/> as it fans out events
/// from <c>audit.events</c> to specific users per the registered
/// <see cref="Services.INotificationRule"/> set.
///
/// <para>
/// Lives in the <c>audit</c> schema (sibling to <c>audit.events</c>) because
/// projections of the audit firehose are conceptually part of the same
/// subsystem and share the same DbContext / migration cadence. Unlike
/// <c>audit.events</c> this table is NOT append-only — users mark rows as
/// read by updating <see cref="ReadAt"/>. Role grants therefore include
/// UPDATE on this single table; DELETE is still withheld (we keep history).
/// </para>
///
/// <para>
/// RLS: tenant-isolated via the standard
/// <c>tenant_isolation_notifications</c> policy (mirrors
/// <c>audit.events</c>). User-isolation is enforced at the LINQ layer by
/// <c>NotificationsEndpoints</c> rather than at the DB layer because there
/// is no <c>app.user_id</c> session setting plumbed today (Sprint 2 / H2 set
/// up tenant-context plumbing only). Adding that setting would require
/// changes to <c>TenantConnectionInterceptor</c> + the identity handler;
/// out of scope for Sprint 8 P3. Tenant-isolation already gives the
/// load-bearing cross-tenant defense; the user-scope LINQ filter is the
/// inner ring.
/// </para>
/// </summary>
public sealed class Notification : ITenantOwned
{
    /// <summary>Surrogate key. Defaults via Postgres <c>gen_random_uuid()</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning tenant. Stamped by <see cref="TenantOwnedEntityInterceptor"/> on insert.</summary>
    public long TenantId { get; set; }

    /// <summary>Recipient. Foreign-key by convention to <c>identity.identity_users.Id</c> (no DB FK — Identity lives in a sibling schema, cross-schema FKs add coupling without buying much).</summary>
    public Guid UserId { get; set; }

    /// <summary>FK to <c>audit.events.EventId</c>. The originating event the projector fanned out from.</summary>
    public Guid EventId { get; set; }

    /// <summary>Mirrors <c>audit.events.EventType</c> so a notification can be filtered without joining.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Short headline rendered in the inbox row.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional body (one or two sentences). May be null for terse rules.</summary>
    public string? Body { get; set; }

    /// <summary>Optional deep-link URL the row's "Follow link" action navigates to.</summary>
    public string? Link { get; set; }

    /// <summary>When the projector wrote the row.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the user marked the row read. Null while unread.</summary>
    public DateTimeOffset? ReadAt { get; set; }
}
