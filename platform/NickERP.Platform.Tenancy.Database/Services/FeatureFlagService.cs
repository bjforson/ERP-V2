using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Entities;
using NickERP.Platform.Tenancy.Features;

namespace NickERP.Platform.Tenancy.Database.Services;

/// <summary>
/// Sprint 35 / B8.2 — default <see cref="IFeatureFlagService"/>
/// backed by <see cref="TenancyDbContext"/>.
///
/// <para>
/// Read path is sparse-row aware: <see cref="IsEnabledAsync"/> returns
/// the persisted value when a row exists and the caller's default
/// otherwise. Write path is upsert: a missing row gets inserted with
/// the new value; an existing row gets its value + audit columns
/// rewritten in place (no DELETE — re-enabling a disabled flag rewrites
/// <c>Enabled = true</c> rather than removing the row, so the audit
/// trail of past flips on the row itself survives).
/// </para>
///
/// <para>
/// Mirrors the pattern Sprint 28 set in
/// <see cref="NickERP.Inspection.Web.Services.RulesAdminService"/> and
/// the Sprint 32 FU-B refit of the module-settings service: audit
/// emission is best-effort (try/catch + log), the upsert is the
/// system-of-record write.
/// </para>
/// </summary>
public sealed class FeatureFlagService : IFeatureFlagService
{
    private readonly TenancyDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IEventPublisher _events;
    private readonly ILogger<FeatureFlagService> _logger;

    public FeatureFlagService(
        TenancyDbContext db,
        TimeProvider clock,
        IEventPublisher events,
        ILogger<FeatureFlagService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(
        string flagKey,
        long tenantId,
        bool defaultValue,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        var normalised = NormaliseKey(flagKey);

        var row = await _db.FeatureFlags
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.FlagKey == normalised, ct);
        return row?.Enabled ?? defaultValue;
    }

    /// <inheritdoc />
    public async Task<FeatureFlagDto> SetAsync(
        string flagKey,
        long tenantId,
        bool enabled,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flagKey);
        var normalised = NormaliseKey(flagKey);

        var existing = await _db.FeatureFlags
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.FlagKey == normalised, ct);
        var now = _clock.GetUtcNow();

        bool oldEnabled;
        if (existing is null)
        {
            // No prior row — synthesise oldEnabled = !enabled so the
            // audit payload reads as a meaningful delta. Same convention
            // as TenantModuleSettingsService (Sprint 32 FU-B).
            oldEnabled = !enabled;
            existing = new FeatureFlag
            {
                TenantId = tenantId,
                FlagKey = normalised,
                Enabled = enabled,
                UpdatedAt = now,
                UpdatedByUserId = actorUserId,
            };
            _db.FeatureFlags.Add(existing);
        }
        else
        {
            oldEnabled = existing.Enabled;
            existing.Enabled = enabled;
            existing.UpdatedAt = now;
            existing.UpdatedByUserId = actorUserId;
        }

        await _db.SaveChangesAsync(ct);

        await EmitToggledEventAsync(existing, oldEnabled, now, ct);

        return new FeatureFlagDto(
            existing.Id,
            existing.TenantId,
            existing.FlagKey,
            existing.Enabled,
            existing.UpdatedAt,
            existing.UpdatedByUserId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeatureFlagDto>> ListAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        var rows = await _db.FeatureFlags
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.FlagKey)
            .ToListAsync(ct);

        return rows.Select(r => new FeatureFlagDto(
            r.Id, r.TenantId, r.FlagKey, r.Enabled, r.UpdatedAt, r.UpdatedByUserId)).ToList();
    }

    private async Task EmitToggledEventAsync(
        FeatureFlag row,
        bool oldEnabled,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                tenantId = row.TenantId,
                flagKey = row.FlagKey,
                enabled = row.Enabled,
                oldEnabled,
                userId = row.UpdatedByUserId,
            });
            var key = IdempotencyKey.ForEntityChange(
                row.TenantId,
                "nickerp.tenancy.feature_flag_toggled",
                "FeatureFlag",
                row.Id.ToString(),
                now);
            var evt = DomainEvent.Create(
                tenantId: row.TenantId,
                actorUserId: row.UpdatedByUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: "nickerp.tenancy.feature_flag_toggled",
                entityType: "FeatureFlag",
                entityId: row.Id.ToString(),
                payload: payload,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit nickerp.tenancy.feature_flag_toggled for tenant {TenantId} flag {FlagKey}",
                row.TenantId, row.FlagKey);
        }
    }

    private static string NormaliseKey(string flagKey)
        => flagKey.Trim().ToLowerInvariant();
}
