using System.Text.Json;
using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Platform.Audit.Database.Services.NotificationRules;

/// <summary>
/// G2 §7 — fans out <c>nickfinance.voucher.requested</c> events into a
/// notification for the box's approver, prompting them to act.
///
/// <para>
/// Reads <c>BoxApproverUserId</c> off the event payload (the field is
/// included by <c>VoucherWorkflowService.RequestAsync</c> for exactly
/// this purpose — the projector has no easy cross-DB read of the box
/// otherwise). If the field is missing or unparseable, the rule emits
/// zero notifications.
/// </para>
/// </summary>
public sealed class VoucherApprovalRequestedRule : INotificationRule
{
    /// <inheritdoc />
    public string EventType => "nickfinance.voucher.requested";

    /// <inheritdoc />
    public Task<IReadOnlyList<Notification>> ProjectAsync(DomainEventRow evt, CancellationToken ct = default)
    {
        if (evt.Payload is null) return Empty();

        if (!TryReadGuid(evt.Payload, "BoxApproverUserId", out var approver))
            return Empty();

        var notification = new Notification
        {
            UserId = approver,
            EventId = evt.EventId,
            EventType = evt.EventType,
            Title = "Voucher needs your approval",
            Body = $"A petty-cash voucher is awaiting your sign-off.",
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
