namespace NickERP.Platform.Tenancy.Features;

/// <summary>
/// Sprint 35 / B8.2 — per-tenant feature flag read/write service.
///
/// <para>
/// Sparse-row convention: missing rows mean "use the caller's
/// default". The service layer does NOT seed defaults — that would
/// freeze the meaning of "default" at provisioning time. Instead,
/// every read goes through <see cref="IsEnabledAsync"/> with an
/// explicit fallback the caller chooses.
/// </para>
///
/// <para>
/// Every <see cref="SetAsync"/> call emits a
/// <c>nickerp.tenancy.feature_flag_toggled</c> audit event so the
/// trail of who-flipped-what survives. Audit emission is best-effort
/// (try/catch + log); the system-of-record write is the upsert.
/// </para>
/// </summary>
public interface IFeatureFlagService
{
    /// <summary>
    /// Read the flag's value for this tenant. Returns
    /// <paramref name="defaultValue"/> when no row exists (sparse).
    /// </summary>
    Task<bool> IsEnabledAsync(
        string flagKey,
        long tenantId,
        bool defaultValue,
        CancellationToken ct = default);

    /// <summary>
    /// Upsert the flag's value for this tenant. Returns the row's
    /// canonical state after the write (so callers can confirm the
    /// flip without a second read). Emits a
    /// <c>nickerp.tenancy.feature_flag_toggled</c> audit event with
    /// payload <c>{tenantId, flagKey, enabled, oldEnabled, userId}</c>.
    /// </summary>
    Task<FeatureFlagDto> SetAsync(
        string flagKey,
        long tenantId,
        bool enabled,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Enumerate every persisted flag row for a tenant, ordered by
    /// flag key. Returns an empty list for a tenant with no rows.
    /// Missing flags from the curated catalogue are populated by the
    /// admin UI; this method only returns what is in the database.
    /// </summary>
    Task<IReadOnlyList<FeatureFlagDto>> ListAsync(
        long tenantId,
        CancellationToken ct = default);
}

/// <summary>One feature flag row exposed via the service layer.</summary>
public sealed record FeatureFlagDto(
    Guid Id,
    long TenantId,
    string FlagKey,
    bool Enabled,
    DateTimeOffset UpdatedAt,
    Guid? UpdatedByUserId);

/// <summary>
/// Sprint 49 / FU-feature-flag-key-validation — thrown by
/// <see cref="IFeatureFlagService.SetAsync"/> when the supplied
/// flag-key does not match the curated regex
/// <c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$</c>.
///
/// <para>The same regex backs the Razor admin form's client-side
/// validation; this exception is the server-side defence-in-depth so a
/// crafted POST cannot bypass the UI check. The keys store stays
/// small + scannable: lowercase alphanumeric, underscore-allowed
/// segments, dot-separated, must have at least one dot.</para>
/// </summary>
public sealed class InvalidFeatureFlagKeyException : ArgumentException
{
    /// <summary>The rejected key (already trimmed + lowercased).</summary>
    public string FlagKey { get; }

    public InvalidFeatureFlagKeyException(string flagKey)
        : base(
            $"Invalid feature flag key '{flagKey}'. Expected pattern " +
            "'^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$' " +
            "(lowercase letters, digits, underscores; dot-separated; at least one dot).",
            paramName: nameof(flagKey))
    {
        FlagKey = flagKey;
    }
}
