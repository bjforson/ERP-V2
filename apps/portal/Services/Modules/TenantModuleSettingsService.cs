using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Portal.Services.Modules;

/// <summary>
/// Sprint 29 — default <see cref="ITenantModuleSettingsService"/> backed
/// by <see cref="TenancyDbContext"/>.
///
/// <para>
/// Sprint 32 FU-B — every successful upsert now emits a
/// <c>nickerp.tenancy.module_toggled</c> audit event so the platform-admin
/// trail of who-toggled-what survives. Mirrors the pattern Sprint 28 set
/// in <see cref="NickERP.Inspection.Web.Services.RulesAdminService"/>:
/// audit-emit is best-effort (try/catch + log), the upsert is the
/// system-of-record write.
/// </para>
/// </summary>
public sealed class TenantModuleSettingsService : ITenantModuleSettingsService
{
    private readonly TenancyDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IEventPublisher _events;
    private readonly ILogger<TenantModuleSettingsService> _logger;

    public TenantModuleSettingsService(
        TenancyDbContext db,
        TimeProvider clock,
        IEventPublisher events,
        ILogger<TenantModuleSettingsService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<TenantModuleSettingDto> SetEnabledAsync(
        long tenantId,
        string moduleId,
        bool enabled,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        var normalisedId = moduleId.Trim().ToLowerInvariant();

        var existing = await _db.TenantModuleSettings
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.ModuleId == normalisedId, ct);
        var now = _clock.GetUtcNow();

        // Capture the prior value BEFORE we mutate so the audit payload
        // can carry oldEnabled. New rows surface as oldEnabled = !enabled
        // by convention — there's no prior state, but the admin trail is
        // most useful when oldEnabled != enabled, so we synthesise the
        // opposite. Callers who care about "first-touch" can disambiguate
        // by also noting the absence of an UpdatedByUserId on the row
        // before this call.
        bool oldEnabled;
        if (existing is null)
        {
            oldEnabled = !enabled;
            existing = new TenantModuleSetting
            {
                TenantId = tenantId,
                ModuleId = normalisedId,
                Enabled = enabled,
                UpdatedAt = now,
                UpdatedByUserId = actorUserId,
            };
            _db.TenantModuleSettings.Add(existing);
        }
        else
        {
            oldEnabled = existing.Enabled;
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedByUserId = actorUserId;
        }

        await _db.SaveChangesAsync(ct);

        // Sprint 32 FU-B — emit nickerp.tenancy.module_toggled. Best-effort:
        // a failed audit write must not roll back the upsert (the
        // system-of-record write already landed). Same shape as Sprint 28's
        // inspection.validation_rule.toggled — camelCase JSON keys,
        // EntityType=TenantModuleSetting, EntityId=existing.Id.ToString().
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                tenantId,
                moduleId = normalisedId,
                enabled,
                oldEnabled,
                userId = actorUserId
            });
            var key = IdempotencyKey.ForEntityChange(
                tenantId,
                "nickerp.tenancy.module_toggled",
                "TenantModuleSetting",
                existing.Id.ToString(),
                now);
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: "nickerp.tenancy.module_toggled",
                entityType: "TenantModuleSetting",
                entityId: existing.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit nickerp.tenancy.module_toggled for tenant {TenantId} module {ModuleId}",
                tenantId, normalisedId);
        }

        return new TenantModuleSettingDto(
            existing.Id,
            existing.TenantId,
            existing.ModuleId,
            existing.Enabled,
            existing.UpdatedAt,
            existing.UpdatedByUserId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantModuleSettingDto>> GetSettingsForTenantAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        var rows = await _db.TenantModuleSettings
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.ModuleId)
            .ToListAsync(ct);

        return rows.Select(r => new TenantModuleSettingDto(
            r.Id, r.TenantId, r.ModuleId, r.Enabled, r.UpdatedAt, r.UpdatedByUserId)).ToList();
    }
}
