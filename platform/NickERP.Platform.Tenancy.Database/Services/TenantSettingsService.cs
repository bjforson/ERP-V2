using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Features;

namespace NickERP.Platform.Tenancy.Database.Services;

/// <summary>
/// Sprint 35 / B8.2 — default <see cref="ITenantSettingsService"/>
/// backed by <see cref="TenancyDbContext"/>.
///
/// <para>
/// Same shape + invariants as <see cref="FeatureFlagService"/> but
/// with a string value: read returns the persisted value or the
/// caller's default; write upserts in place and emits a
/// <c>nickerp.tenancy.setting_changed</c> audit event with payload
/// <c>{tenantId, settingKey, value, oldValue, userId}</c>.
/// </para>
///
/// <para>
/// The <see cref="GetIntAsync"/> helper exists so common
/// integer-typed settings (port numbers, retention windows, default
/// budgets) don't have to repeat the parse + fallback pattern at
/// every call site. Parse failures (malformed value or empty string)
/// fall back to the caller's default and log at <c>Warning</c>.
/// </para>
/// </summary>
public sealed class TenantSettingsService : ITenantSettingsService
{
    private readonly TenancyDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IEventPublisher _events;
    private readonly ILogger<TenantSettingsService> _logger;

    public TenantSettingsService(
        TenancyDbContext db,
        TimeProvider clock,
        IEventPublisher events,
        ILogger<TenantSettingsService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<string> GetAsync(
        string settingKey,
        long tenantId,
        string defaultValue,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        ArgumentNullException.ThrowIfNull(defaultValue);

        var normalised = NormaliseKey(settingKey);
        var row = await _db.TenantSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.SettingKey == normalised, ct);
        return row?.Value ?? defaultValue;
    }

    /// <inheritdoc />
    public async Task<int> GetIntAsync(
        string settingKey,
        long tenantId,
        int defaultValue,
        CancellationToken ct = default)
    {
        var raw = await GetAsync(settingKey, tenantId, string.Empty, ct);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        _logger.LogWarning(
            "Tenant setting {SettingKey} for tenant {TenantId} has non-integer value '{Value}'; falling back to {Default}",
            settingKey, tenantId, raw, defaultValue);
        return defaultValue;
    }

    /// <inheritdoc />
    public async Task<TenantSettingDto> SetAsync(
        string settingKey,
        long tenantId,
        string value,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        ArgumentNullException.ThrowIfNull(value);

        var normalised = NormaliseKey(settingKey);
        var existing = await _db.TenantSettings
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.SettingKey == normalised, ct);
        var now = _clock.GetUtcNow();

        string oldValue;
        if (existing is null)
        {
            oldValue = string.Empty;
            existing = new TenantSetting
            {
                TenantId = tenantId,
                SettingKey = normalised,
                Value = value,
                UpdatedAt = now,
                UpdatedByUserId = actorUserId,
            };
            _db.TenantSettings.Add(existing);
        }
        else
        {
            oldValue = existing.Value;
            existing.Value = value;
            existing.UpdatedAt = now;
            existing.UpdatedByUserId = actorUserId;
        }

        await _db.SaveChangesAsync(ct);

        await EmitChangedEventAsync(existing, oldValue, now, ct);

        return new TenantSettingDto(
            existing.Id,
            existing.TenantId,
            existing.SettingKey,
            existing.Value,
            existing.UpdatedAt,
            existing.UpdatedByUserId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantSettingDto>> ListAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        var rows = await _db.TenantSettings
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.SettingKey)
            .ToListAsync(ct);

        return rows.Select(r => new TenantSettingDto(
            r.Id, r.TenantId, r.SettingKey, r.Value, r.UpdatedAt, r.UpdatedByUserId)).ToList();
    }

    private async Task EmitChangedEventAsync(
        TenantSetting row,
        string oldValue,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                tenantId = row.TenantId,
                settingKey = row.SettingKey,
                value = row.Value,
                oldValue,
                userId = row.UpdatedByUserId,
            });
            var key = IdempotencyKey.ForEntityChange(
                row.TenantId,
                "nickerp.tenancy.setting_changed",
                "TenantSetting",
                row.Id.ToString(),
                now);
            var evt = DomainEvent.Create(
                tenantId: row.TenantId,
                actorUserId: row.UpdatedByUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: "nickerp.tenancy.setting_changed",
                entityType: "TenantSetting",
                entityId: row.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit nickerp.tenancy.setting_changed for tenant {TenantId} setting {SettingKey}",
                row.TenantId, row.SettingKey);
        }
    }

    private static string NormaliseKey(string settingKey)
        => settingKey.Trim().ToLowerInvariant();
}
