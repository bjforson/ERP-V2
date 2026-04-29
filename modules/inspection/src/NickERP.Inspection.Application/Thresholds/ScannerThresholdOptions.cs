namespace NickERP.Inspection.Application.Thresholds;

/// <summary>
/// Configuration for the per-scanner threshold resolver (§6.5).
/// Bound to <c>NickErp:Inspection:Thresholds</c> in <c>appsettings.json</c>.
/// </summary>
public sealed class ScannerThresholdOptions
{
    public const string SectionName = "NickErp:Inspection:Thresholds";

    /// <summary>
    /// PostgreSQL channel name (§6.5.3). Trigger on UPDATE of
    /// <c>scanner_threshold_profiles.Status</c> sends
    /// <c>(scannerDeviceInstanceId, version)</c>; the resolver subscribes
    /// once at host startup and evicts the cached entry on receipt.
    /// </summary>
    public string NotifyChannel { get; set; } = "threshold_profile_updated";

    /// <summary>
    /// Belt-and-braces fallback TTL (§6.5.8). Even with NOTIFY working,
    /// every cached entry expires after this duration; covers the rare
    /// case of a dropped notification or a re-subscribe gap. 1 h matches
    /// the spec's ceiling.
    /// </summary>
    public TimeSpan FallbackTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long the LISTEN/NOTIFY backplane waits between reconnect
    /// attempts after the connection drops. Doubles up to
    /// <see cref="ListenReconnectMaxBackoff"/> on consecutive failures;
    /// resets on a successful subscribe.
    /// </summary>
    public TimeSpan ListenReconnectInitialBackoff { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Cap for the exponential reconnect backoff.</summary>
    public TimeSpan ListenReconnectMaxBackoff { get; set; } = TimeSpan.FromMinutes(2);
}
