using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Per-<see cref="ExternalSystemInstance"/> pull cursor for the inbound
/// post-hoc outcome adapter (§6.11.8). One row per external system
/// instance running in pull or hybrid mode.
///
/// <para>
/// The cursor advances <em>only</em> on full success of the page-iteration
/// loop — partial advances would risk silent gaps. Idempotency at the
/// document layer makes replay safe even on rollback.
/// </para>
///
/// <para>
/// Pull window:
/// <list type="bullet">
/// <item><c>since = LastPullWindowUntil - 24h</c> — the 24 h overlap
/// absorbs authority-side late commits.</item>
/// <item><c>until = now() - 5min</c> — skew buffer so we never pull rows
/// the authority hasn't had a moment to settle.</item>
/// </list>
/// </para>
///
/// <para>
/// <see cref="ConsecutiveFailures"/> is incremented on every failed pull
/// and reset to zero on success. The
/// <c>posthoc_pull_cursor_lag_seconds{instance}</c> Prometheus gauge alarms
/// at &gt; 6 h; at &gt; 24 h the runbook escalates (§6.11.14
/// authentication-rotation-outage guard).
/// </para>
/// </summary>
public sealed class OutcomePullCursor : ITenantOwned
{
    /// <summary>
    /// The external system this cursor tracks. Primary key — exactly one
    /// cursor per instance.
    /// </summary>
    public Guid ExternalSystemInstanceId { get; set; }
    public ExternalSystemInstance? ExternalSystemInstance { get; set; }

    /// <summary>When the most recent successful pull-cycle completed.</summary>
    public DateTimeOffset LastSuccessfulPullAt { get; set; }

    /// <summary>
    /// Upper bound of the most recent successful pull window. The next
    /// pull cycle uses <c>(LastPullWindowUntil - 24h, now() - 5min]</c>.
    /// </summary>
    public DateTimeOffset LastPullWindowUntil { get; set; }

    /// <summary>Reset to zero on every successful pull. Drives the lag-seconds gauge / runbook escalation.</summary>
    public int ConsecutiveFailures { get; set; }

    public long TenantId { get; set; }
}
