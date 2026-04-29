using System.Text.Json;
using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Platform.Audit.Database.Services.NotificationRules;

/// <summary>
/// G2 §7 — fans out <c>nickfinance.voucher.approved</c> events into a
/// notification for the original requester, telling them their voucher
/// has been signed off and is ready for the custodian to disburse.
/// </summary>
public sealed class VoucherApprovedRule : INotificationRule
{
    /// <inheritdoc />
    public string EventType => "nickfinance.voucher.approved";

    /// <inheritdoc />
    public Task<IReadOnlyList<Notification>> ProjectAsync(DomainEventRow evt, CancellationToken ct = default)
    {
        if (evt.Payload is null) return Empty();

        if (!TryReadGuid(evt.Payload, "RequestedByUserId", out var requester))
            return Empty();

        var notification = new Notification
        {
            UserId = requester,
            EventId = evt.EventId,
            EventType = evt.EventType,
            Title = "Voucher approved",
            Body = "Your petty-cash voucher has been approved; the custodian can now disburse.",
            Link = $"/finance/petty-cash/vouchers/{evt.EntityId}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        return Task.FromResult<IReadOnlyList<Notification>>(new[] { notification });
    }

    private static Task<IReadOnlyList<Notification>> Empty()
        => Task.FromResult<IReadOnlyList<Notification>>(Array.Empty<Notification>());

    private static bool TryReadGuid(JsonDocument payload, string propertyName, out Guid value)
    {
        value = default;
        if (!payload.RootElement.TryGetProperty(propertyName, out var prop))
            return false;
        if (prop.ValueKind == JsonValueKind.String)
            return Guid.TryParse(prop.GetString(), out value);
        return false;
    }
}
