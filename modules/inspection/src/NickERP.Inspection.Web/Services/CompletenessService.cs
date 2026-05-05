using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.Completeness;
using NickERP.Inspection.Core.Completeness;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 31 / B5.1 — admin-side service backing the
/// <c>/admin/completeness</c> Razor page.
///
/// <para>
/// Owns the read + write paths for
/// <see cref="TenantCompletenessSetting"/>: list registered
/// requirements with the current tenant's enabled state + threshold
/// override + recent miss counts; flip the enabled flag for a specific
/// requirement + emit an audit event so the trail of who-disabled-what
/// survives. Mirrors the Sprint 28 <see cref="RulesAdminService"/>
/// shape and reuses the same audit-event vocabulary
/// (<c>inspection.completeness_setting.toggled</c>).
/// </para>
/// </summary>
public sealed class CompletenessService
{
    private readonly TenancyDbContext _tenancyDb;
    private readonly AuditDbContext _auditDb;
    private readonly CompletenessChecker _checker;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ILogger<CompletenessService> _logger;

    public CompletenessService(
        TenancyDbContext tenancyDb,
        AuditDbContext auditDb,
        CompletenessChecker checker,
        ITenantContext tenant,
        IEventPublisher events,
        ILogger<CompletenessService> logger)
    {
        _tenancyDb = tenancyDb ?? throw new ArgumentNullException(nameof(tenancyDb));
        _auditDb = auditDb ?? throw new ArgumentNullException(nameof(auditDb));
        _checker = checker ?? throw new ArgumentNullException(nameof(checker));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Enumerate every registered requirement with the tenant's enabled
    /// flag + threshold override + recent-miss count for the admin
    /// index page. Falls back to "enabled, no threshold" when no
    /// per-tenant override row exists (sparse storage convention).
    /// </summary>
    public async Task<IReadOnlyList<CompletenessRequirementRow>> ListRequirementsAsync(
        long tenantId,
        TimeSpan? missLookback = null,
        CancellationToken ct = default)
    {
        var lookback = missLookback ?? TimeSpan.FromDays(7);
        var since = DateTimeOffset.UtcNow - lookback;

        var settings = await _tenancyDb.TenantCompletenessSettings
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);
        var byReq = settings.ToDictionary(
            s => s.RequirementId,
            StringComparer.OrdinalIgnoreCase);

        // Pull miss counts from the audit stream — events match either
        // 'incomplete' or 'partially_complete' types. Bounded materialise
        // then group in memory keeps the EF query simple and avoids
        // jsonb-LINQ translation surprises (same posture as RulesAdminService).
        var failed = await _auditDb.Events
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId
                     && e.OccurredAt >= since
                     && (e.EventType == "inspection.completeness.incomplete"
                      || e.EventType == "inspection.completeness.partially_complete"))
            .Select(e => new { e.EventId, e.Payload, e.OccurredAt })
            .ToListAsync(ct);

        var missCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in failed)
        {
            var requirementId = ExtractRequirementId(f.Payload);
            if (requirementId is null) continue;
            missCounts.TryGetValue(requirementId, out var c);
            missCounts[requirementId] = c + 1;
        }

        var rows = new List<CompletenessRequirementRow>(_checker.Requirements.Count);
        foreach (var req in _checker.Requirements)
        {
            byReq.TryGetValue(req.RequirementId, out var setting);
            missCounts.TryGetValue(req.RequirementId, out var mc);
            rows.Add(new CompletenessRequirementRow(
                RequirementId: req.RequirementId,
                Description: req.Description,
                Enabled: setting?.Enabled ?? true,
                MinThreshold: setting?.MinThreshold,
                HasOverride: setting is not null,
                LastUpdatedAt: setting?.UpdatedAt,
                LastUpdatedByUserId: setting?.UpdatedByUserId,
                RecentMissCount: mc));
        }
        return rows;
    }

    /// <summary>
    /// Toggle the enabled flag for a requirement. Emits the
    /// <c>inspection.completeness_setting.toggled</c> audit event;
    /// upserts so re-enabling rewrites Enabled=true rather than
    /// deleting the row (preserves the audit trail of past flips).
    /// </summary>
    public async Task SetRequirementEnabledAsync(
        long tenantId,
        string requirementId,
        bool enabled,
        decimal? minThreshold,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requirementId);

        var known = _checker.Requirements.Any(
            r => string.Equals(r.RequirementId, requirementId, StringComparison.OrdinalIgnoreCase));
        if (!known)
            throw new InvalidOperationException(
                $"Requirement '{requirementId}' is not registered. Refusing to write a tenant override for an unknown requirement.");

        var lower = requirementId.ToLowerInvariant();
        var existing = await _tenancyDb.TenantCompletenessSettings
            .FirstOrDefaultAsync(
                s => s.TenantId == tenantId
                  && s.RequirementId.ToLower() == lower,
                ct);
        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            existing = new TenantCompletenessSetting
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                RequirementId = requirementId,
                Enabled = enabled,
                MinThreshold = minThreshold,
                UpdatedAt = now,
                UpdatedByUserId = actorUserId
            };
            _tenancyDb.TenantCompletenessSettings.Add(existing);
        }
        else
        {
            existing.Enabled = enabled;
            existing.MinThreshold = minThreshold;
            existing.UpdatedAt = now;
            existing.UpdatedByUserId = actorUserId;
        }
        await _tenancyDb.SaveChangesAsync(ct);

        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                requirementId,
                enabled,
                minThreshold,
                tenantId
            });
            var key = IdempotencyKey.ForEntityChange(
                tenantId,
                "inspection.completeness_setting.toggled",
                "TenantCompletenessSetting",
                existing.Id.ToString(),
                now);
            var evt = DomainEvent.Create(
                tenantId,
                actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: "inspection.completeness_setting.toggled",
                entityType: "TenantCompletenessSetting",
                entityId: existing.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit completeness_setting.toggled for tenant {TenantId} requirement {RequirementId}",
                tenantId, requirementId);
        }
    }

    private static string? ExtractRequirementId(JsonDocument? payload)
    {
        if (payload is null) return null;
        if (!payload.RootElement.TryGetProperty("requirementId", out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }
}

/// <summary>
/// Sprint 31 / B5.1 — admin row for the completeness page.
/// </summary>
public sealed record CompletenessRequirementRow(
    string RequirementId,
    string Description,
    bool Enabled,
    decimal? MinThreshold,
    bool HasOverride,
    DateTimeOffset? LastUpdatedAt,
    Guid? LastUpdatedByUserId,
    int RecentMissCount);
