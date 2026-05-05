namespace NickERP.Platform.Tenancy.Features;

/// <summary>
/// Sprint 35 / B8.2 — generic per-tenant key/value setting service.
/// Mirrors <see cref="IFeatureFlagService"/> but with a string value
/// instead of a boolean. Use this for tenant-scoped configuration that
/// doesn't justify its own typed entity (default SLA budgets,
/// retention windows, comms-gateway endpoints, etc.).
///
/// <para>
/// Sparse rows + caller-supplied defaults match
/// <see cref="IFeatureFlagService"/> exactly. Every <see cref="SetAsync"/>
/// emits a <c>nickerp.tenancy.setting_changed</c> audit event with
/// <c>{tenantId, settingKey, value, oldValue, userId}</c>.
/// </para>
/// </summary>
public interface ITenantSettingsService
{
    /// <summary>
    /// Read the setting's value for this tenant. Returns
    /// <paramref name="defaultValue"/> when no row exists (sparse).
    /// Whitespace-only values are returned verbatim — the service does
    /// not normalise.
    /// </summary>
    Task<string> GetAsync(
        string settingKey,
        long tenantId,
        string defaultValue,
        CancellationToken ct = default);

    /// <summary>
    /// Read the setting's value as <see langword="int"/>. Returns
    /// <paramref name="defaultValue"/> when no row exists or the row's
    /// value cannot be parsed as <see langword="int"/>.
    /// </summary>
    Task<int> GetIntAsync(
        string settingKey,
        long tenantId,
        int defaultValue,
        CancellationToken ct = default);

    /// <summary>
    /// Upsert the setting's value for this tenant. Returns the row's
    /// canonical state after the write. Emits a
    /// <c>nickerp.tenancy.setting_changed</c> audit event.
    /// </summary>
    Task<TenantSettingDto> SetAsync(
        string settingKey,
        long tenantId,
        string value,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerate every persisted setting row for a tenant, ordered by
    /// setting key. Returns an empty list for a tenant with no rows.
    /// </summary>
    Task<IReadOnlyList<TenantSettingDto>> ListAsync(
        long tenantId,
        CancellationToken ct = default);
}

/// <summary>One tenant-setting row exposed via the service layer.</summary>
public sealed record TenantSettingDto(
    Guid Id,
    long TenantId,
    string SettingKey,
    string Value,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId);
