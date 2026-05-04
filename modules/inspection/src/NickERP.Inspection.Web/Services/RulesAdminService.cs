using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.Validation;
using NickERP.Inspection.Core.Validation;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 28 / B4 Phase C — admin-side service backing the
/// <c>/admin/rules</c> + <c>/admin/rules/{ruleId}</c> Razor pages.
///
/// <para>
/// Owns the read + write paths for
/// <see cref="TenantValidationRuleSetting"/>: list registered rules with
/// the current tenant's enabled state + recent failure counts; flip the
/// enabled flag for a specific rule + emit an audit event so the trail
/// of who-disabled-what survives.
/// </para>
///
/// <para>
/// Reads recent failures from <c>audit.events</c> filtered by
/// <c>EventType IN (inspection.validation.failed, ...skipped)</c> +
/// <c>Payload-&gt;&gt;'ruleId' = {ruleId}</c>. The drill-down page surfaces
/// the case ids so the analyst can hop straight to the case detail.
/// </para>
/// </summary>
public sealed class RulesAdminService
{
    private readonly TenancyDbContext _tenancyDb;
    private readonly AuditDbContext _auditDb;
    private readonly ValidationEngine _engine;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ILogger<RulesAdminService> _logger;

    public RulesAdminService(
        TenancyDbContext tenancyDb,
        AuditDbContext auditDb,
        ValidationEngine engine,
        ITenantContext tenant,
        IEventPublisher events,
        ILogger<RulesAdminService> logger)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
        _auditDb = auditDb ?? throw new ArgumentNullException(nameof(auditDb));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Enumerate every registered rule with the current tenant's
    /// enabled flag + a recent-failure count for the admin index page.
    /// Falls back to "enabled" when no per-tenant override row exists
    /// (sparse storage convention).
    /// </summary>
    public async Task<IReadOnlyList<RuleAdminRow>> ListRulesAsync(
        long tenantId,
        TimeSpan? failureLookback = null,
        CancellationToken ct = default)
    {
        var lookback = failureLookback ?? TimeSpan.FromDays(7);
        var since = DateTimeOffset.UtcNow - lookback;

        var settings = await _tenancyDb.TenantValidationRuleSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);
        var byRule = settings.ToDictionary(
            s => s.RuleId,
            StringComparer.OrdinalIgnoreCase);

        // Pull failure counts in one query — group by ruleId across the
        // failed-event window. Audit Payload is jsonb; the LINQ-translated
        // EF query reads ruleId via the EF.Functions.JsonValue helper, but
        // some EF/Npgsql versions struggle with that. Materialise the
        // window then group in memory — safer + the row count is small.
        var failed = await _auditDb.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                     && e.OccurredAt >= since
                     && e.EventType == "inspection.validation.failed")
            .Select(e => new { e.EventId, e.Payload, e.OccurredAt })
            .ToListAsync(ct);

        var failureCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in failed)
        {
            var ruleId = ExtractRuleId(f.Payload);
            if (ruleId is null) continue;
            failureCounts.TryGetValue(ruleId, out var c);
            failureCounts[ruleId] = c + 1;
        }

        var rows = new List<RuleAdminRow>(_engine.Rules.Count);
        foreach (var rule in _engine.Rules)
        {
            byRule.TryGetValue(rule.RuleId, out var setting);
            failureCounts.TryGetValue(rule.RuleId, out var fc);
            rows.Add(new RuleAdminRow(
                RuleId: rule.RuleId,
                Description: rule.Description,
                Enabled: setting?.Enabled ?? true,
                HasOverride: setting is not null,
                LastUpdatedAt: setting?.UpdatedAt,
                LastUpdatedByUserId: setting?.UpdatedByUserId,
                RecentFailureCount: fc));
        }
        return rows;
    }

    /// <summary>
    /// Toggle the enabled flag for a rule. Emits the
    /// <c>inspection.validation_rule.toggled</c> audit event whether or
    /// not the underlying row already existed; upserts the row so a
    /// re-enable after a disable rewrites <see cref="TenantValidationRuleSetting.Enabled"/>=true
    /// instead of deleting the row (preserves the audit trail of past
    /// flips on the row itself).
    /// </summary>
    public async Task SetRuleEnabledAsync(
        long tenantId,
        string ruleId,
        bool enabled,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);

        // Validate the rule exists in the registry — refuse to write a
        // setting row for a non-existent rule (would dangle on rule
        // removal).
        var known = _engine.Rules.Any(
            r => string.Equals(r.RuleId, ruleId, StringComparison.OrdinalIgnoreCase));
        if (!known)
            throw new InvalidOperationException(
                $"Rule '{ruleId}' is not registered. Refusing to write a tenant override for an unknown rule.");

        // Lower-case both sides for the lookup so the unique-index
        // (TenantId, RuleId) is honoured while staying case-insensitive
        // across the in-memory test provider + Postgres production.
        var ruleIdLower = ruleId.ToLowerInvariant();
        var existing = await _tenancyDb.TenantValidationRuleSettings
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                  && s.RuleId.ToLower() == ruleIdLower,
                ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            existing = new TenantValidationRuleSetting
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RuleId = ruleId,
                Enabled = enabled,
                UpdatedAt = now,
                UpdatedByUserId = actorUserId
            };
            _tenancyDb.TenantValidationRuleSettings.Add(existing);
        }
        else
        {
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedByUserId = actorUserId;
        }
        await _tenancyDb.SaveChangesAsync(ct);

        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                ruleId,
                enabled,
                tenantId
            });
            var key = IdempotencyKey.ForEntityChange(
                tenantId,
                "inspection.validation_rule.toggled",
                "TenantValidationRuleSetting",
                existing.Id.ToString(),
                now);
            var evt = DomainEvent.Create(
                tenantId,
                actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: "inspection.validation_rule.toggled",
                entityType: "TenantValidationRuleSetting",
                entityId: existing.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit validation_rule.toggled for tenant {TenantId} rule {RuleId}",
                tenantId, ruleId);
        }
    }

    /// <summary>
    /// Pull the most recent N failure events for one rule on one tenant
    /// for the drill-down page. Returns the case ids + occurrence
    /// timestamps so the page can hyperlink to /cases/{id}.
    /// </summary>
    public async Task<IReadOnlyList<RuleFailureSnapshot>> RecentFailuresAsync(
        long tenantId,
        string ruleId,
        int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0) take = 50;
        var rows = await _auditDb.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                     && e.EventType == "inspection.validation.failed"
                     && e.EntityType == "InspectionCase")
            .OrderByDescending(e => e.OccurredAt)
            .Take(take * 4) // overshoot then filter; payload-level filter is in-memory
            .Select(e => new { e.Payload, e.EntityId, e.OccurredAt })
            .ToListAsync(ct);

        var matches = rows
            .Where(r => string.Equals(ExtractRuleId(r.Payload), ruleId, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .Select(r => new RuleFailureSnapshot(
                CaseId: r.EntityId,
                OccurredAt: r.OccurredAt,
                Severity: ExtractSeverity(r.Payload) ?? "Error",
                Message: ExtractMessage(r.Payload) ?? string.Empty))
            .ToList();

        return matches;
    }

    private static string? ExtractRuleId(JsonDocument? payload)
        => ExtractString(payload, "ruleId");

    private static string? ExtractSeverity(JsonDocument? payload)
        => ExtractString(payload, "severity");

    private static string? ExtractMessage(JsonDocument? payload)
        => ExtractString(payload, "message");

    private static string? ExtractString(JsonDocument? payload, string key)
    {
        if (payload is null) return null;
        if (!payload.RootElement.TryGetProperty(key, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }
}

/// <summary>
/// Sprint 28 — per-rule row for the admin index page. Carries the rule
/// metadata + the current tenant's override + the recent failure count.
/// </summary>
public sealed record RuleAdminRow(
    string RuleId,
    string Description,
    bool Enabled,
    bool HasOverride,
    DateTimeOffset? LastUpdatedAt,
    Guid? LastUpdatedByUserId,
    int RecentFailureCount);

/// <summary>One audit row for the rule-detail drill-down.</summary>
public sealed record RuleFailureSnapshot(
    string CaseId,
    DateTimeOffset OccurredAt,
    string Severity,
    string Message);
