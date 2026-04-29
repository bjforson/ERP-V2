using System.Text.Json;
using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Platform.Audit.Database.Services.NotificationRules;

/// <summary>
/// Sprint 8 P3 — fans out <c>nickerp.inspection.case_assigned</c> events
/// into a notification for the assigned analyst.
///
/// <para>
/// Reads the analyst's <c>UserId</c> from the event payload's
/// <c>AnalystUserId</c> property. The shape comes from
/// <c>CaseWorkflowService.AssignToCurrentUserAsync</c> which emits
/// <c>{ Id, AnalystUserId }</c>. If the property is missing or
/// unparseable, the rule emits zero notifications and logs nothing —
/// the projector treats absent rule output as benign.
/// </para>
/// </summary>
public sealed class CaseAssignedRule : INotificationRule
{
    /// <inheritdoc />
    public string EventType => "nickerp.inspection.case_assigned";

    /// <inheritdoc />
    public Task<IReadOnlyList<Notification>> ProjectAsync(DomainEventRow evt, CancellationToken ct = default)
    {
        // Defensive: payload may be empty / shape may have drifted. Bail
        // quietly rather than throwing — the projector continues with
        // the next rule.
        if (evt.Payload is null) return Empty();

        if (!TryReadGuid(evt.Payload, "AnalystUserId", out var analyst))
            return Empty();

        var notification = new Notification
        {
            UserId = analyst,
            EventId = evt.EventId,
            EventType = evt.EventType,
            Title = "Case assigned to you",
            Body = $"You have been assigned to inspection case {evt.EntityId}.",
            Link = $"/cases/{evt.EntityId}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        return Task.FromResult<IReadOnlyList<Notification>>(new[] { notification });
    }

    private static Task<IReadOnlyList<Notification>> Empty()
        => Task.FromResult<IReadOnlyList<Notification>>(Array.Empty<Notification>());

    /// <summary>
    /// Read a Guid-shaped property from the event's JSON payload. Returns
    /// false on missing key, non-string value, or unparseable Guid.
    /// </summary>
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
