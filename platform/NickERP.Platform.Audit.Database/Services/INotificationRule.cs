using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Platform.Audit.Database.Services;

/// <summary>
/// A rule that fans out a single <see cref="DomainEventRow"/> into zero
/// or more <see cref="Notification"/> rows. Sprint 8 P3 ships three
/// hardcoded rules (case-opened, case-assigned, verdict-rendered); a
/// future per-user subscription UI will register additional rules
/// dynamically.
///
/// <para>
/// Rule contract:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="EventType"/> filter — the projector
///         only invokes a rule whose <see cref="EventType"/> matches the
///         row's <c>EventType</c> column. Fast filter; avoids materialising
///         payload JSON for every event.</description></item>
///   <item><description><see cref="ProjectAsync"/> returns the
///         notifications to insert. The projector handles tenant context
///         + persistence + idempotency, so rules are pure mappings.</description></item>
/// </list>
///
/// <para>
/// Rules MUST be idempotent: the projector may re-invoke the same rule
/// for the same event after a crash before the checkpoint advanced. The
/// unique <c>(UserId, EventId)</c> index on <c>audit.notifications</c>
/// makes accidental re-inserts a benign no-op (DbUpdateException caught
/// by the projector).
/// </para>
/// </summary>
public interface INotificationRule
{
    /// <summary>
    /// The exact <c>EventType</c> string this rule reacts to.
    /// Must match <see cref="DomainEventRow.EventType"/> case-sensitively.
    /// </summary>
    string EventType { get; }

    /// <summary>
    /// Compute the notifications this rule wants to insert for
    /// <paramref name="evt"/>. Implementations may inspect the JSON
    /// payload, the actor user id, the entity id; they MUST NOT mutate
    /// <paramref name="evt"/>.
    /// </summary>
    /// <param name="evt">The originating audit event.</param>
    /// <param name="ct">Cancellation token tied to the projector tick.</param>
    /// <returns>
    /// Zero or more notifications to insert. Each must have its
    /// <c>EventId</c> set to <paramref name="evt"/>'s
    /// <see cref="DomainEventRow.EventId"/>; <c>EventType</c> mirrored;
    /// <c>TenantId</c> stamped (or left zero — the
    /// <c>TenantOwnedEntityInterceptor</c> will stamp it from the projector's
    /// tenant scope on save).
    /// </returns>
    Task<IReadOnlyList<Notification>> ProjectAsync(DomainEventRow evt, CancellationToken ct = default);
}
