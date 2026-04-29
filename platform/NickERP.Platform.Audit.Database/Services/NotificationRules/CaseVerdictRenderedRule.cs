using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Platform.Audit.Database.Services.NotificationRules;

/// <summary>
/// Sprint 8 P3 — fans out <c>nickerp.inspection.verdict_set</c> events
/// into a notification for the case opener.
///
/// <para>
/// The verdict event payload is
/// <c>{ Id, CaseId, Decision, Basis, ConfidenceScore }</c> — it does NOT
/// carry the case-opener's <c>UserId</c>, so we look it up via the audit
/// log: the originating <c>nickerp.inspection.case_opened</c> event for
/// the same case is the source of truth (and is naturally tenant-scoped
/// because RLS narrows the lookup to the projector's current tenant).
/// </para>
///
/// <para>
/// If no <c>case_opened</c> row is found (orphan / pre-projection
/// audit), the rule emits zero notifications. If the opener is the same
/// person who decided the verdict (one-person tenant), we still notify
/// — the inbox row doubles as a "your verdict is recorded" confirmation
/// and avoids special-casing.
/// </para>
/// </summary>
public sealed class CaseVerdictRenderedRule : INotificationRule
{
    private readonly AuditDbContext _audit;

    public CaseVerdictRenderedRule(AuditDbContext audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public string EventType => "nickerp.inspection.verdict_set";

    /// <inheritdoc />
    public async Task<IReadOnlyList<Notification>> ProjectAsync(
        DomainEventRow evt,
        CancellationToken ct = default)
    {
        // verdict_set's EntityId is the verdict's Guid; the case id lives
        // in the payload. We match the case_opened audit row by the
        // payload's CaseId field (entity_type=InspectionCase, entity_id=CaseId).
        if (evt.Payload is null) return Array.Empty<Notification>();

        if (!evt.Payload.RootElement.TryGetProperty("CaseId", out var caseIdProp)) return Array.Empty<Notification>();
        if (caseIdProp.ValueKind != System.Text.Json.JsonValueKind.String) return Array.Empty<Notification>();
        if (!Guid.TryParse(caseIdProp.GetString(), out var caseId)) return Array.Empty<Notification>();

        var caseEntityId = caseId.ToString();

        // Look up the originating case_opened event. RLS narrows to the
        // projector's current tenant scope, so cross-tenant leakage is
        // not possible even if a malicious payload set a foreign CaseId.
        var openedActor = await _audit.Events
            .AsNoTracking()
            .Where(e => e.EventType == "nickerp.inspection.case_opened"
                     && e.EntityType == "InspectionCase"
                     && e.EntityId == caseEntityId
                     && e.ActorUserId != null)
            .OrderBy(e => e.OccurredAt)
            .Select(e => e.ActorUserId)
            .FirstOrDefaultAsync(ct);

        if (openedActor is not Guid opener) return Array.Empty<Notification>();

        var notification = new Notification
        {
            UserId = opener,
            EventId = evt.EventId,
            EventType = evt.EventType,
            Title = "Verdict rendered",
            Body = $"A verdict has been rendered on inspection case {caseEntityId}.",
            Link = $"/cases/{caseEntityId}",
            CreatedAt = DateTimeOffset.UtcNow
        };

        return new[] { notification };
    }
}
