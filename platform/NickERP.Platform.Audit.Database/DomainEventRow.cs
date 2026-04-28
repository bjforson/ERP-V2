using System.Text.Json;

namespace NickERP.Platform.Audit.Database;

/// <summary>
/// EF-mapped row shape for <c>audit.events</c>. Mirrors
/// <see cref="Audit.Events.DomainEvent"/> but is mutable to satisfy EF's
/// materialisation. Public so the audit-admin UI (Portal) can project
/// rows directly; module code mostly works with <see cref="Audit.Events.DomainEvent"/>.
/// </summary>
public sealed class DomainEventRow
{
    public Guid EventId { get; set; }
    /// <summary>
    /// Owning tenant. Nullable so the audit log can carry events that are
    /// not scoped to a single tenant (suite-wide FX rates, global chart of
    /// accounts updates). Module code that emits per-tenant events always
    /// supplies a concrete value.
    /// </summary>
    public long? TenantId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? CorrelationId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public JsonDocument Payload { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset IngestedAt { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? PrevEventHash { get; set; }
}
