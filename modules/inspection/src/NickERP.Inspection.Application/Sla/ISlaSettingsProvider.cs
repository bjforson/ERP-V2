namespace NickERP.Inspection.Application.Sla;

/// <summary>
/// Sprint 31 / B5.1 — per-tenant SLA budget lookup. The
/// <see cref="SlaTracker"/> reads this once per OpenWindowsAsync call
/// to fetch all overrides at once; missing rows fall back to
/// <see cref="SlaTrackerOptions.DefaultBudgets"/>.
/// </summary>
public interface ISlaSettingsProvider
{
    /// <summary>
    /// Bulk-fetch every override row for the tenant, keyed by window
    /// name (case-insensitive). Empty result = "use engine defaults
    /// for every window".
    /// </summary>
    Task<IReadOnlyDictionary<string, SlaSettingSnapshot>> GetSettingsAsync(
        long tenantId,
        CancellationToken ct = default);
}

/// <summary>
/// Sprint 31 / B5.1 — bulk-fetch shape for
/// <see cref="ISlaSettingsProvider.GetSettingsAsync"/>. Carries the
/// (Enabled, TargetMinutes) pair for one (tenant, window) row.
/// </summary>
public sealed record SlaSettingSnapshot(bool Enabled, int TargetMinutes);
