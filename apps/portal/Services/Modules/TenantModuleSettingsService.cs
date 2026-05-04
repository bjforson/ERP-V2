using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Portal.Services.Modules;

/// <summary>
/// Sprint 29 — default <see cref="ITenantModuleSettingsService"/> backed
/// by <see cref="TenancyDbContext"/>.
/// </summary>
public sealed class TenantModuleSettingsService : ITenantModuleSettingsService
{
    private readonly TenancyDbContext _db;
    private readonly TimeProvider _clock;

    public TenantModuleSettingsService(TenancyDbContext db, TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
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

        if (existing is null)
        {
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
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedByUserId = actorUserId;
        }

        await _db.SaveChangesAsync(ct);

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
