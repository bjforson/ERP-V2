using System.Text.Json;

namespace NickERP.Platform.Audit.Database;

/// <summary>
/// EF-mapped row shape for <c>audit.events</c>. Mirrors
/// <see cref="Audit.Events.DomainEvent"/> but is mutable to satisfy EF's
/// materialisation. Application code never sees this — it sees
/// <see cref="Audit.Events.DomainEvent"/>; mapping happens inside
/// <see cref="DbEventPublisher"/>.
/// </summary>
internal sealed class DomainEventRow
{
    public Guid EventId { get; set; }
    public long TenantId { get; set; }
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
