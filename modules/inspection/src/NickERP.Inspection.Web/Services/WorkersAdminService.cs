using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.Workers;
using NickERP.Platform.Telemetry;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 50 / FU-b3-admin-pages — admin service backing the
/// <c>/admin/workers</c> Razor page. Reflects over every registered
/// <see cref="IBackgroundServiceProbe"/> + maps to the matching options
/// blob + the worker's force-tick method.
///
/// <para>
/// <b>Read + force-tick only.</b> The page surfaces tick state +
/// telemetry-derived counts; runtime enable/disable is intentionally
/// NOT exposed because flipping a flag at runtime would surprise ops
/// (the worker is configured by ops, not by a self-service admin
/// click). For "stop the worker", flip the config + redeploy.
/// </para>
///
/// <para>
/// <b>Curated worker list.</b> The set of workers is hand-maintained
/// because the force-tick method name varies per worker
/// (<c>ScanOnceAsync</c>, <c>PullOnceAsync</c>, <c>SweepOnceAsync</c>,
/// etc). A reflective scan would have to encode that variability
/// anyway; the explicit table below makes the mapping legible + the
/// admin UI's behaviour stays predictable as workers are added/removed.
/// New workers add themselves here in the same patch that registers
/// them in <c>Program.cs</c>.
/// </para>
/// </summary>
public class WorkersAdminService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WorkersAdminService> _logger;

    /// <summary>
    /// Mapping of probe name → (worker concrete type, options binder, force-tick
    /// method name). The force-tick method is invoked via reflection so
    /// internal/public access modifiers don't matter for the admin path.
    /// </summary>
    private static readonly IReadOnlyList<WorkerEntry> Entries = new[]
    {
        new WorkerEntry(
            ProbeName: "ScannerIngestionWorker",
            WorkerType: typeof(ScannerIngestionWorker),
            OptionsType: null,
            ForceTickMethod: null,
            Description: "Streams scan artifacts from every active filesystem-watcher scanner (FS6000)."),

        new WorkerEntry(
            ProbeName: "OutcomePullWorker",
            WorkerType: typeof(OutcomePullWorker),
            OptionsType: typeof(NickERP.Inspection.Application.PostHocOutcomes.OutcomeIngestionOptions),
            ForceTickMethod: "DrainOnceAsync",
            Description: "Sprint 13 § 6.11 — pulls post-hoc outcomes from configured PostHocRolloutPhase rows."),

        new WorkerEntry(
            ProbeName: "ScannerHealthSweepWorker",
            WorkerType: typeof(ScannerHealthSweepWorker),
            OptionsType: typeof(ScannerHealthSweepOptions),
            ForceTickMethod: "SweepOnceAsync",
            Description: "Periodic IScannerAdapter.TestAsync sweep across every active scanner."),

        new WorkerEntry(
            ProbeName: "AseSyncWorker",
            WorkerType: typeof(AseSyncWorker),
            OptionsType: typeof(AseSyncOptions),
            ForceTickMethod: "PullOnceAsync",
            Description: "Cursor-based pull from every IScannerCursorSyncAdapter (ASE / future cloud APIs)."),

        new WorkerEntry(
            ProbeName: "AuthorityDocumentBackfillWorker",
            WorkerType: typeof(AuthorityDocumentBackfillWorker),
            OptionsType: typeof(IcumsApiPullOptions),
            ForceTickMethod: "BackfillOnceAsync",
            Description: "Periodic per-tenant fetch of authority documents via IExternalSystemAdapter.FetchDocumentsAsync."),

        new WorkerEntry(
            ProbeName: "AuthorityDocumentInboxWorker",
            WorkerType: typeof(AuthorityDocumentInboxWorker),
            OptionsType: typeof(IcumsFileScannerOptions),
            ForceTickMethod: "ScanOnceAsync",
            Description: "Per-tenant subfolder watcher for authority drop-folder JSON exports."),

        new WorkerEntry(
            ProbeName: "OutboundSubmissionDispatchWorker",
            WorkerType: typeof(OutboundSubmissionDispatchWorker),
            OptionsType: typeof(IcumsSubmissionDispatchOptions),
            ForceTickMethod: "DispatchOnceAsync",
            Description: "Dispatches OutboundSubmission rows in pending status with bounded retry."),

        new WorkerEntry(
            ProbeName: "OutboundSubmissionResultPollerWorker",
            WorkerType: typeof(OutboundSubmissionResultPollerWorker),
            OptionsType: typeof(IcumsSubmissionResultPollerOptions),
            ForceTickMethod: "PollOnceAsync",
            Description: "Re-polls in-flight submissions for deferred outcomes."),

        new WorkerEntry(
            ProbeName: "AuthorityDocumentMatcherWorker",
            WorkerType: typeof(AuthorityDocumentMatcherWorker),
            OptionsType: typeof(ContainerDataMatcherOptions),
            ForceTickMethod: "MatchOnceAsync",
            Description: "Matches Scan rows to AuthorityDocument rows by container number + capture window."),

        new WorkerEntry(
            ProbeName: "SlaStateRefresherWorker",
            WorkerType: typeof(SlaStateRefresherWorker),
            OptionsType: typeof(SlaStateRefresherOptions),
            ForceTickMethod: "RefreshOnceAsync",
            Description: "Periodic SLA window state refresh — flips OnTime → AtRisk → Breached + emits audit events."),

        new WorkerEntry(
            ProbeName: "RetentionEnforcerWorker",
            WorkerType: typeof(RetentionEnforcerWorker),
            OptionsType: typeof(RetentionEnforcerOptions),
            ForceTickMethod: "SweepOnceAsync",
            Description: "Surfaces retention-purge candidates per tenant (audit-only; no auto-delete)."),

        new WorkerEntry(
            ProbeName: "WebhookDispatchWorker",
            WorkerType: typeof(WebhookDispatchWorker),
            OptionsType: typeof(WebhookDispatchOptions),
            ForceTickMethod: "DispatchOnceAsync",
            Description: "Dispatches new audit events to every registered IOutboundWebhookAdapter per tenant."),

        new WorkerEntry(
            ProbeName: "QueueEscalatorWorker",
            WorkerType: typeof(QueueEscalatorWorker),
            OptionsType: typeof(QueueEscalatorOptions),
            ForceTickMethod: "EscalateOnceAsync",
            Description: "Auto-escalates SLA window queue tier from Standard → High → Urgent on time-since-StartedAt."),
    };

    public WorkersAdminService(
        IServiceProvider services,
        ILogger<WorkersAdminService> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Snapshot every known worker's name + enabled flag + last-tick
    /// state + telemetry counter values. Used by the
    /// <c>/admin/workers</c> page to populate the row list.
    ///
    /// <para>
    /// Workers whose probe isn't registered (e.g. a future deploy that
    /// strips a module) surface <see cref="WorkerRowDto.IsRegistered"/> = false
    /// + <c>null</c> state. Probes registered with no matching curated
    /// entry (a worker that hasn't been added to
    /// <see cref="Entries"/> yet) surface as a row tagged
    /// <c>"unregistered-in-admin"</c> so ops sees the gap.
    /// </para>
    /// </summary>
    public virtual IReadOnlyList<WorkerRowDto> GetWorkerSnapshot()
    {
        var probes = _services.GetServices<IBackgroundServiceProbe>().ToList();
        var probesByName = probes.ToDictionary(p => p.WorkerName, StringComparer.Ordinal);

        var rows = new List<WorkerRowDto>(Entries.Count + probes.Count);

        foreach (var entry in Entries)
        {
            probesByName.TryGetValue(entry.ProbeName, out var probe);
            var state = probe?.GetState();
            var enabled = ResolveEnabled(entry.OptionsType);

            rows.Add(new WorkerRowDto(
                Name: entry.ProbeName,
                Description: entry.Description,
                IsRegistered: probe is not null,
                Enabled: enabled,
                ForceTickAvailable: entry.ForceTickMethod is not null && probe is not null,
                Health: state?.Health.ToString(),
                LastTickAt: state?.LastTickAt,
                LastSuccessAt: state?.LastSuccessAt,
                TickCount: state?.TickCount ?? 0,
                ErrorCount: state?.ErrorCount ?? 0,
                LastError: state?.LastError,
                LastErrorAt: state?.LastErrorAt,
                Note: probe is null ? "probe not registered (worker absent in this host)" : null));
        }

        // Surface any probe registered in DI but not present in the
        // curated table. Should be empty — but if a new worker lands
        // before the admin page is updated, ops sees the row + the
        // "unregistered-in-admin" note instead of the worker silently
        // disappearing.
        var knownNames = Entries.Select(e => e.ProbeName).ToHashSet(StringComparer.Ordinal);
        foreach (var probe in probes)
        {
            if (knownNames.Contains(probe.WorkerName)) continue;
            var s = probe.GetState();
            rows.Add(new WorkerRowDto(
                Name: probe.WorkerName,
                Description: "(unregistered in admin)",
                IsRegistered: true,
                Enabled: null,
                ForceTickAvailable: false,
                Health: s.Health.ToString(),
                LastTickAt: s.LastTickAt,
                LastSuccessAt: s.LastSuccessAt,
                TickCount: s.TickCount,
                ErrorCount: s.ErrorCount,
                LastError: s.LastError,
                LastErrorAt: s.LastErrorAt,
                Note: "unregistered-in-admin"));
        }

        return rows
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Force one tick of the named worker. Returns the count the tick
    /// method returned (typically "records processed") OR -1 when the
    /// worker has no force-tick wired (e.g. ScannerIngestionWorker — the
    /// streaming worker; force-tick would mean "kick the inner stream"
    /// and is a different shape). Throws when the worker is absent.
    /// </summary>
    public virtual async Task<int> ForceTickAsync(string workerName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);

        var entry = Entries.FirstOrDefault(e =>
            string.Equals(e.ProbeName, workerName, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new InvalidOperationException(
                $"No curated worker entry for '{workerName}'.");
        }
        if (entry.ForceTickMethod is null)
        {
            return -1;
        }

        // Resolve the worker singleton. The DI registration uses the
        // concrete type as the key (Program.cs registers it via
        // AddSingleton<TWorker>() then forwards to hosted-service +
        // probe slots), so we resolve the same instance the host runs.
        var worker = _services.GetService(entry.WorkerType);
        if (worker is null)
        {
            throw new InvalidOperationException(
                $"Worker '{entry.WorkerType.Name}' is not registered in DI.");
        }

        var method = entry.WorkerType.GetMethod(
            entry.ForceTickMethod,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (method is null)
        {
            throw new InvalidOperationException(
                $"Worker '{entry.WorkerType.Name}' has no method '{entry.ForceTickMethod}'.");
        }

        _logger.LogInformation(
            "WorkersAdmin force-tick: {WorkerName}.{Method}", entry.ProbeName, entry.ForceTickMethod);

        // ForceTick methods are uniformly Task<int>. If a future worker
        // returns Task we'd surface 0; we don't have any today.
        var result = method.Invoke(worker, new object?[] { ct });
        if (result is Task<int> typed)
        {
            return await typed;
        }
        if (result is Task plain)
        {
            await plain;
            return 0;
        }
        throw new InvalidOperationException(
            $"Worker '{entry.WorkerType.Name}.{entry.ForceTickMethod}' returned an unexpected type {result?.GetType().FullName}.");
    }

    /// <summary>
    /// Resolve <see cref="WorkerOptionsBase.Enabled"/> for the worker's
    /// options type via <c>IOptionsMonitor&lt;TOptions&gt;</c>. Uses a
    /// closed-generic <c>typeof(IOptionsMonitor&lt;&gt;).MakeGenericType(...)</c>
    /// because we don't know the type at compile time.
    /// </summary>
    private bool? ResolveEnabled(Type? optionsType)
    {
        if (optionsType is null) return null;
        try
        {
            var monitorType = typeof(IOptionsMonitor<>).MakeGenericType(optionsType);
            var monitor = _services.GetService(monitorType);
            if (monitor is null) return null;

            var current = monitorType.GetProperty(nameof(IOptionsMonitor<object>.CurrentValue))
                ?.GetValue(monitor);
            if (current is null) return null;
            if (current is WorkerOptionsBase wob) return wob.Enabled;
            // OutcomeIngestionOptions / RetryOptions etc. don't derive
            // from WorkerOptionsBase — surface null so the page renders
            // "—" instead of guessing.
            var enabledProp = current.GetType().GetProperty("Enabled");
            if (enabledProp?.GetValue(current) is bool b) return b;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WorkersAdmin: failed to resolve options for {Type}.", optionsType.FullName);
            return null;
        }
    }

    private sealed record WorkerEntry(
        string ProbeName,
        Type WorkerType,
        Type? OptionsType,
        string? ForceTickMethod,
        string Description);
}

/// <summary>
/// One row on the /admin/workers page. Public for tests + page
/// rendering.
/// </summary>
public sealed record WorkerRowDto(
    string Name,
    string Description,
    bool IsRegistered,
    bool? Enabled,
    bool ForceTickAvailable,
    string? Health,
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastSuccessAt,
    long TickCount,
    long ErrorCount,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    string? Note);
