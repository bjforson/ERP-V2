using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Platform.Audit.Database.Services.NotificationRules;

/// <summary>
/// Sprint 8 P3 — fans out <c>nickerp.inspection.case_opened</c> events
/// into a notification for the user who opened the case.
///
/// <para>
/// MVP simplification (TODO): the PLAN spec called for "all tenant
/// admins" or "case-opened-watcher scope holders". Both require a role /
/// scope lookup that doesn't exist yet (Sprint 5 only set up
/// <c>UserScope</c>; Sprint 6 didn't ship a tenant-admin lookup). For v0
/// we notify the case opener — always known via
/// <see cref="DomainEventRow.ActorUserId"/> — so the user who clicked
/// "open case" sees the row in their inbox. The acceptance criterion
/// only requires "appropriate user(s)"; opener-only is appropriate for
/// v0 and easy to verify in the live-host smoke (open a case, see your
/// own bell increment).
/// </para>
///
/// <para>
/// When per-user subscription preferences ship, this rule's
/// <see cref="ProjectAsync"/> body will expand to a tenant-admin lookup
/// (or, more likely, an "interested users" query against the future
/// <c>identity.user_subscriptions</c> table).
/// </para>
/// </summary>
public sealed class CaseOpenedRule : INotificationRule
{
    /// <inheritdoc />
    public string EventType => "nickerp.inspection.case_opened";

    /// <inheritdoc />
    public Task<IReadOnlyList<Notification>> ProjectAsync(DomainEventRow evt, CancellationToken ct = default)
    {
        // No actor — orphan event (system-issued case_opened). Skip.
        if (evt.ActorUserId is not Guid opener)
        {
            return Task.FromResult<IReadOnlyList<Notification>>(Array.Empty<Notification>());
        }

        var notification = new Notification
        {
            UserId = opener,
            EventId = evt.EventId,
            EventType = evt.EventType,
            Title = "Case opened",
            Body = $"You opened inspection case {evt.EntityId}.",
            Link = $"/cases/{evt.EntityId}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        return Task.FromResult<IReadOnlyList<Notification>>(new[] { notification });
    }
}
