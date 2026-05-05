namespace NickERP.Inspection.Application.Workers;

/// <summary>
/// Sprint 24 / B3 — common shape every B3 hosted worker binds. Each
/// concrete worker has its own derived options class with the same
/// keys + worker-specific extras; this base type centralises the
/// <c>Enabled</c> + <c>PollInterval</c> contract so the host always
/// surfaces the same flags.
///
/// <para>
/// <b>Default-disabled.</b> Per Sprint 24 architectural decisions,
/// every B3 worker defaults to <see cref="Enabled"/> = <c>false</c>.
/// A fresh deploy doesn't auto-start polling something that doesn't
/// exist (no FS6000 watch folder, no ICUMS endpoint configured, etc.) —
/// ops opts in per environment via config or env-var.
/// </para>
///
/// <para>
/// <b>Config keys.</b> Each derived options class binds under a section
/// of <c>Inspection:Workers:&lt;Name&gt;:</c> in <c>appsettings.json</c>;
/// env-vars follow the standard <c>Inspection__Workers__&lt;Name&gt;__Enabled</c>
/// shape.
/// </para>
/// </summary>
public abstract class WorkerOptionsBase
{
    /// <summary>
    /// Master enable. <b>Defaults to false.</b> Workers that run without
    /// specific deployment config (FS6000 watch folder, ICUMS endpoint
    /// URL, ASE connection string) would no-op anyway, but skipping the
    /// loop entirely keeps logs quieter and probes "Healthy" rather
    /// than perpetually "ticked but did nothing".
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Per-cycle poll interval. Defaults to 1 minute — fine for
    /// ops-tier polling; per-worker derived classes may override.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// First-cycle delay so the host has time to finish migrations,
    /// plugin discovery, DB warmup before the worker tries to do real
    /// work. Defaults to 15s (matches OutcomePullWorker).
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Sprint 24 / B3.1 — options for <c>ScannerHealthSweepWorker</c>.
/// Periodically calls <c>IScannerAdapter.TestAsync()</c> on every
/// active scanner instance and records the result on the worker's
/// telemetry counter. Surfaces vendor connectivity loss without
/// waiting for an inbound scan to fail.
/// </summary>
public sealed class ScannerHealthSweepOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:ScannerHealthSweep";

    /// <summary>
    /// Override poll interval — health sweeps are cheap (one
    /// <c>TestAsync</c> per device) but the adapter call may go to a
    /// remote service so we don't hammer it. Default 5 minutes.
    /// </summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Sprint 24 / B3.1 — options for <c>AseSyncWorker</c>.
/// Cursor-based pull from any <c>IScannerCursorSyncAdapter</c>
/// (default vendor: ASE; the worker is vendor-neutral).
/// </summary>
public sealed class AseSyncOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:AseSync";

    /// <summary>
    /// Per-cycle poll interval. ASE sync is cheaper than file-watcher
    /// streaming because it batches; default 2 minutes.
    /// </summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Soft upper bound on the per-pull batch size. Adapter may return
    /// fewer; never returns more.
    /// </summary>
    public int BatchLimit { get; set; } = 100;

    /// <summary>
    /// Drain quota per cycle — if the adapter signals <c>HasMore=true</c>,
    /// the worker may keep pulling within one cycle, but stops at this
    /// many records to give other workers a turn.
    /// </summary>
    public int MaxRecordsPerCycle { get; set; } = 500;
}

/// <summary>
/// Sprint 24 / B3.2 — options for <c>IcumsApiPullWorker</c>.
/// Periodic per-tenant pull from ICUMS REST API. Replaces
/// v1 <c>IcumBackgroundService</c>.
/// </summary>
public sealed class IcumsApiPullOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:IcumsApiPull";

    /// <summary>
    /// Default 15 minutes — matches v1's NSCIM cadence
    /// (BatchIntervalMinutes=30 / 2 polls per batch).
    /// </summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Window overlap on every pull — same shape as
    /// <c>OutcomeIngestionOptions.WindowOverlap</c>. Default 24h.
    /// </summary>
    public TimeSpan WindowOverlap { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Sprint 24 / B3.2 — options for <c>IcumsFileScannerWorker</c>.
/// Watches a configurable filesystem drop folder for ICUMS XML/JSON
/// exports. Replaces v1 <c>IcumFileScannerService</c>.
/// </summary>
public sealed class IcumsFileScannerOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:IcumsFileScanner";

    /// <summary>
    /// Default 1 minute — match v1's
    /// <c>IcumFileScannerService.ScanIntervalMinutes</c> default.
    /// </summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Filesystem path to watch. <c>null</c> or empty = no-op cycle
    /// (logs once at startup; subsequent ticks are silent). Per-tenant
    /// override goes in <c>ExternalSystemInstance.ConfigJson</c>; this
    /// is the host-default fallback.
    /// </summary>
    public string? DropFolder { get; set; }

    /// <summary>
    /// Subfolders the v1 contract expects under <see cref="DropFolder"/>.
    /// Files outside these subfolders are ignored. Mirrors v1 line 71-82
    /// of <c>IcumFileScannerService.ScanForNewFilesAsync</c>.
    /// </summary>
    public IReadOnlyList<string> ExpectedSubfolders { get; set; } = new[]
    {
        "BatchData", "ContainerData", "ScanResults", "StatusChecks"
    };
}

/// <summary>
/// Sprint 24 / B3.2 — options for <c>IcumsSubmissionDispatchWorker</c>.
/// Reads <c>OutboundSubmission.Status='pending'</c> rows + dispatches
/// to ICUMS via the <c>IExternalSystemAdapter.SubmitAsync</c> path.
/// Replaces v1 <c>ICUMSSubmissionService</c>.
/// </summary>
public sealed class IcumsSubmissionDispatchOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:IcumsSubmissionDispatch";

    /// <summary>Default 1 minute.</summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Max pending submissions to dispatch per cycle.</summary>
    public int BatchLimit { get; set; } = 50;
}

/// <summary>
/// Sprint 24 / B3.2 — options for <c>IcumsSubmissionResultPollerWorker</c>.
/// Polls ICUMS for the outcome of submissions previously dispatched
/// (<c>Status='accepted'</c> + <c>RespondedAt is null</c> or pending
/// confirmation). Replaces v1 <c>ICUMSDownloadBackgroundService</c>.
/// </summary>
public sealed class IcumsSubmissionResultPollerOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:IcumsSubmissionResultPoller";

    /// <summary>Default 5 minutes.</summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Max in-flight submissions to poll per cycle.</summary>
    public int BatchLimit { get; set; } = 50;
}

/// <summary>
/// Sprint 24 / B3.2 — options for <c>ContainerDataMatcherWorker</c>.
/// Periodically matches <c>Scan</c> rows to <c>AuthorityDocument</c>
/// rows by container number + capture-window. Replaces v1
/// <c>ContainerDataMapperService</c>.
/// </summary>
public sealed class ContainerDataMatcherOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:ContainerDataMatcher";

    /// <summary>Default 2 minutes.</summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Capture-window tolerance for matching a Scan to an
    /// AuthorityDocument: the document's <c>ReceivedAt</c> may be up to
    /// this many hours before / after the Scan's <c>CapturedAt</c>.
    /// Default 24h (matches v1's "same trade day" heuristic).
    /// </summary>
    public TimeSpan CaptureWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Max scans to match per cycle.</summary>
    public int BatchLimit { get; set; } = 200;
}

/// <summary>
/// Sprint 36 / FU-outbound-dispatch-retry — bounded retry budget +
/// exponential backoff for the
/// <c>OutboundSubmissionDispatchWorker</c>. Pre-Sprint-36, transient
/// adapter failures (network blips, authority HTTP 5xx) flipped the
/// submission to <c>Status='error'</c> and required an operator
/// requeue. With this options block, the dispatcher requeues the
/// submission back to <c>pending</c> with an exponential backoff
/// (<see cref="BaseBackoff"/> * 2^<c>RetryCount</c>, capped at
/// <see cref="MaxBackoff"/>, plus jitter); only after
/// <see cref="MaxRetries"/> consecutive failures does the row flip to
/// <c>error</c> for operator triage.
///
/// <para>
/// <b>Bound by design.</b> Unbounded retry would mask configuration
/// regressions (wrong endpoint, expired credential, authority taking
/// the system down) — the dispatcher would burn CPU re-trying forever.
/// Five attempts with exponential backoff covers a 30-second blip and
/// a 10-minute blip without burning the queue forever; the operator UI
/// surfaces error rows for re-investigation.
/// </para>
///
/// <para>
/// Bound under <c>Inspection:Workers:OutboundSubmissionDispatch:Retry</c>
/// — sub-section of the dispatch worker's existing options block. Hosts
/// not configuring this section get the defaults below.
/// </para>
/// </summary>
public sealed class OutboundSubmissionRetryOptions
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:OutboundSubmissionDispatch:Retry";

    /// <summary>
    /// Maximum number of transient-failure retries before the
    /// dispatcher gives up and flips the submission to <c>error</c>.
    /// Default 5; counted across the row's lifetime (operator requeue
    /// resets to 0).
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Backoff base. The Nth retry waits roughly
    /// <c>BaseBackoff * 2^N</c> before becoming eligible again, capped
    /// at <see cref="MaxBackoff"/> and perturbed by ±25% jitter so a
    /// thundering-herd of pending rows doesn't all retry on the same
    /// tick. Default 30 seconds.
    /// </summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cap on the per-retry backoff. Without this, the 5th retry's
    /// exponential delay reaches ~16 minutes — long enough that a
    /// genuine 30-second blip stays stuck. Default 1 hour (matches the
    /// v1 admin requeue cadence).
    /// </summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Sprint 36 / FU-sla-state-refresher-worker — periodic state-refresher
/// worker for the SLA window engine. The Sprint 31 dashboard service
/// calls <c>SlaTracker.RefreshStatesAsync</c> on every dashboard query
/// path so the screen reflects "as-of-now" lifecycle bucket
/// (OnTime/AtRisk/Breached). That path keeps the dashboard fresh but
/// never writes the transition back — meaning the audit-event /
/// notification path that fires on Breached transitions never trips
/// until the dashboard is loaded.
///
/// <para>
/// This worker promotes the refresh into a periodic
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that
/// scans every active tenant + every still-open window + writes the
/// recomputed state back. Audit event
/// <c>inspection.sla.state_refreshed</c> fires per tenant per tick when
/// at least one window flipped state.
/// </para>
///
/// <para>
/// <b>Default-disabled</b> per Sprint 24 architectural decision; opt-in
/// per environment via
/// <c>Inspection:Workers:SlaStateRefresher:Enabled=true</c>.
/// </para>
/// </summary>
public sealed class SlaStateRefresherOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:SlaStateRefresher";

    /// <summary>
    /// Override poll interval. Default 60 seconds — fine-grained enough
    /// that AtRisk → Breached transitions fire within a minute of the
    /// budget-expiry, cheap enough that the once-per-tenant DbContext
    /// scope stays inexpensive.
    /// </summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Sprint 44 / Phase B — options for the periodic retention enforcer.
/// Walks every active tenant + every closed case eligible for the
/// auto-purge surface (Standard + Extended classes), reports purge
/// candidates per tenant per tick. Does NOT delete; surfaces candidates
/// for an operator-driven hard-purge decision (post-pilot, out of
/// scope for Sprint 44). Same posture as Sprint 18
/// <c>TenantPurgeOrchestrator</c>.
///
/// <para>
/// <b>Cadence.</b> Default 6 hours — the retention windows are 5-7
/// years (Standard / Extended fallbacks); a 6-hour sweep catches
/// transitions within the same business day. Cheap because the
/// candidate-counting query is index-friendly (TenantId + RetentionClass
/// + ClosedAt) and per-tenant; idle tenants short-circuit at zero.
/// </para>
///
/// <para>
/// <b>Default-disabled</b> per Sprint 24 architectural decision; opt-in
/// per environment via
/// <c>Inspection:Workers:RetentionEnforcer:Enabled=true</c>.
/// </para>
/// </summary>
public sealed class RetentionEnforcerOptions : WorkerOptionsBase
{
    /// <summary>Section root binding key.</summary>
    public const string SectionName = "Inspection:Workers:RetentionEnforcer";

    /// <summary>
    /// Override poll interval. Default 6 hours — the retention windows
    /// are 5-7 years (default Standard / Extended fallbacks); a 6-hour
    /// sweep catches transitions within the same business day. Faster
    /// cadences are wasteful; slower cadences leave compliance windows
    /// open longer than needed.
    /// </summary>
    public new TimeSpan PollInterval { get; set; } = TimeSpan.FromHours(6);
}
