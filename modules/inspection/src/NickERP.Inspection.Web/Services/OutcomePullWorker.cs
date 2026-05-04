using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Inbound post-hoc outcome adapter pull worker (§6.11.2).
///
/// <para>
/// Drives every active <see cref="PostHocRolloutPhase"/> row (phase ≥
/// <see cref="PostHocRolloutPhaseValue.Shadow"/>) by resolving its
/// <see cref="ExternalSystemInstance"/>'s <see cref="IInboundOutcomeAdapter"/>
/// plugin, computing the pull window from the row's
/// <see cref="OutcomePullCursor"/>, and invoking
/// <see cref="IInboundOutcomeAdapter.FetchOutcomesAsync"/>. This commit
/// scaffolds the orchestration loop only — persistence and cursor
/// advancement land in commit 2.
/// </para>
///
/// <para>
/// Mirrors <see cref="ScannerIngestionWorker"/>'s shape:
/// <list type="bullet">
/// <item>Cross-tenant discovery via <see cref="TenancyDbContext"/>.Tenants
/// (the only un-RLS'd table on the platform DB).</item>
/// <item>Per-iteration scope from <see cref="IServiceScopeFactory"/> —
/// plugin singleton + scoped DbContext is the FU-icums-signing /
/// ScannerThresholdResolver pattern (see
/// <c>handoff-2026-04-29-rolling-master-session.md</c> §5).</item>
/// <item>One slow / crashing authority cannot wedge the whole worker —
/// outer try/catch logs and continues to the next phase row.</item>
/// </list>
/// </para>
///
/// <para>
/// Phases (canonical naming from <see cref="PostHocRolloutPhaseValue"/>;
/// the brief uses approximate names but the entity values are the source
/// of truth):
/// <list type="number">
/// <item>0 — DevEvalManualOnly: skipped on cycle.</item>
/// <item>1 — Shadow: pulled and persisted, but training signal NOT
/// emitted to <see cref="AnalystReview.PostHocOutcomeJson"/>.</item>
/// <item>2 — PrimaryPlus5PctAudit: full path enabled.</item>
/// <item>3 — Primary: full path + supersession-correction overrides.</item>
/// </list>
/// </para>
///
/// <para>
/// Single-host-only for v0 — running multiple replicas would race on the
/// cursor advance. The cursor's <see cref="OutcomePullCursor.LastPullWindowUntil"/>
/// is the contention point; an advisory lock on
/// <c>(external_system_instance_id)</c> would lift this restriction in
/// the durable-queue sprint (ARCHITECTURE §7.7), out of scope here.
/// </para>
/// </summary>
public sealed class OutcomePullWorker : BackgroundService, IBackgroundServiceProbe
{
    private readonly IServiceProvider _services;
    private readonly IOptions<OutcomeIngestionOptions> _options;
    private readonly ILogger<OutcomePullWorker> _logger;
    private readonly TimeProvider _clock;

    private readonly BackgroundServiceProbeState _probe = new();

    public OutcomePullWorker(
        IServiceProvider services,
        IOptions<OutcomeIngestionOptions> options,
        ILogger<OutcomePullWorker> logger,
        TimeProvider? clock = null)
    {
        _services = services;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string WorkerName => nameof(OutcomePullWorker);

    /// <inheritdoc />
    public BackgroundServiceState GetState() => _probe.Snapshot();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        if (!opts.Enabled)
        {
            _logger.LogInformation(
                "OutcomePullWorker disabled via PostHocOutcomes:Enabled=false; not starting.");
            return;
        }

        _probe.SetPollInterval(opts.PullInterval);
        _logger.LogInformation(
            "OutcomePullWorker starting — pulling every {Interval}, startup delay {Delay}.",
            opts.PullInterval, opts.StartupDelay);

        try { await Task.Delay(opts.StartupDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _probe.RecordTickStart();
            try
            {
                var pulled = await DrainOnceAsync(stoppingToken);
                _probe.RecordTickSuccess();
                _logger.LogInformation(
                    "OutcomePullWorker cycle complete — {Count} (phase, instance) pairs visited.",
                    pulled);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _probe.RecordTickFailure(ex);
                _logger.LogError(ex,
                    "OutcomePullWorker cycle failed; will retry in {Interval}.",
                    opts.PullInterval);
            }

            try { await Task.Delay(opts.PullInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>
    /// One cycle: walk every active tenant, find every phase row in
    /// <see cref="PostHocRolloutPhaseValue.Shadow"/> or above, resolve
    /// its adapter, pull. Returns the count of (phase, instance) pairs
    /// the cycle attempted (success or failure both count — the probe
    /// uses tick success/failure separately).
    /// </summary>
    /// <remarks>
    /// Internal so the test project can drive a single cycle without
    /// taking the worker through the full hosted-service start dance.
    /// </remarks>
    internal async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        // Cross-tenant discovery — same shape as ScannerIngestionWorker.
        // Walk every active tenant (tenancy.tenants is intentionally not
        // under RLS), then for each tenant pull the phase rows under that
        // tenant's RLS context.
        var phaseRows = await DiscoverActivePhaseRowsAsync(ct);
        if (phaseRows.Count == 0)
        {
            return 0;
        }

        var pulled = 0;
        foreach (var row in phaseRows)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await PullForPhaseAsync(row, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One bad authority must not kill the cycle — log and
                // continue to the next. The cursor stays put on failure
                // so the next cycle replays the same window (the
                // adapter's idempotency contract makes that safe).
                _logger.LogError(ex,
                    "OutcomePullWorker pull failed for tenant={TenantId} instance={InstanceId} phase={Phase}; cursor unchanged.",
                    row.TenantId, row.ExternalSystemInstanceId, row.Phase);
            }

            pulled++;
        }

        return pulled;
    }

    /// <summary>
    /// Walk every active tenant in <see cref="TenancyDbContext"/> and
    /// collect phase rows in phase ≥ <see cref="PostHocRolloutPhaseValue.Shadow"/>.
    /// Phase 0 (DevEvalManualOnly) is skipped — the manual-entry tool
    /// is the only ingest path at that phase.
    /// </summary>
    private async Task<IReadOnlyList<PhaseDescriptor>> DiscoverActivePhaseRowsAsync(CancellationToken ct)
    {
        var results = new List<PhaseDescriptor>();

        using var discoveryScope = _services.CreateScope();
        var sp = discoveryScope.ServiceProvider;
        var tenantsDb = sp.GetRequiredService<TenancyDbContext>();
        var tenant = sp.GetRequiredService<ITenantContext>();
        var inspectionDb = sp.GetRequiredService<InspectionDbContext>();

        // Sprint 18 — IsActive is now a computed property; query the
        // backing State column so EF can translate.
        var activeTenantIds = await tenantsDb.Tenants
            .AsNoTracking()
            .Where(t => t.State == TenantState.Active)
            .Select(t => t.Id)
            .ToListAsync(ct);

        foreach (var tenantId in activeTenantIds)
        {
            ct.ThrowIfCancellationRequested();

            tenant.SetTenant(tenantId);

            // Force a fresh connection so the connection interceptor
            // re-pushes app.tenant_id with the new value (the pooled
            // connection might still carry the previous tenant). Same
            // dance as ScannerIngestionWorker.
            try
            {
                if (inspectionDb.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                    await inspectionDb.Database.CloseConnectionAsync();
            }
            catch { /* best-effort */ }

            var rows = await inspectionDb.Set<PostHocRolloutPhase>()
                .AsNoTracking()
                .Include(p => p.ExternalSystemInstance)
                .Where(p => p.CurrentPhase >= PostHocRolloutPhaseValue.Shadow
                            && p.ExternalSystemInstance!.IsActive)
                .Select(p => new PhaseDescriptor(
                    p.TenantId,
                    p.ExternalSystemInstanceId,
                    p.ExternalSystemInstance!.TypeCode,
                    p.ExternalSystemInstance.ConfigJson,
                    p.CurrentPhase))
                .ToListAsync(ct);

            results.AddRange(rows);
        }

        return results;
    }

    /// <summary>
    /// Pull for one (tenant, instance, phase) row. Plugin singleton +
    /// scoped DbContext: this opens its own scope so the captured
    /// <see cref="InspectionDbContext"/> + <see cref="ITenantContext"/>
    /// don't outlive the call.
    /// </summary>
    private async Task PullForPhaseAsync(PhaseDescriptor descriptor, CancellationToken ct)
    {
        if (!RolloutPhasePolicy.ShouldPullOnCycle(descriptor.Phase))
        {
            // Phase 0 already filtered upstream; defensive only.
            return;
        }

        using var scope = _services.CreateScope();
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var plugins = sp.GetRequiredService<IPluginRegistry>();
        var db = sp.GetRequiredService<InspectionDbContext>();
        var writer = sp.GetRequiredService<IPostHocOutcomeWriter>();

        tenant.SetTenant(descriptor.TenantId);

        IInboundOutcomeAdapter adapter;
        try
        {
            adapter = plugins.Resolve<IInboundOutcomeAdapter>(
                "inspection", descriptor.TypeCode, sp);
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning(
                "OutcomePullWorker: no plugin registered for typeCode={TypeCode} (tenant={TenantId} instance={InstanceId}); skipping.",
                descriptor.TypeCode, descriptor.TenantId, descriptor.ExternalSystemInstanceId);
            return;
        }
        catch (InvalidOperationException ex)
        {
            // Plugin exists but doesn't implement IInboundOutcomeAdapter
            // — capability flag mismatch. Operator misconfiguration.
            _logger.LogWarning(ex,
                "OutcomePullWorker: plugin '{TypeCode}' does not implement IInboundOutcomeAdapter; (tenant={TenantId} instance={InstanceId}) skipping.",
                descriptor.TypeCode, descriptor.TenantId, descriptor.ExternalSystemInstanceId);
            return;
        }

        if (!adapter.Capabilities.SupportsOutcomePull)
        {
            _logger.LogDebug(
                "OutcomePullWorker: adapter '{TypeCode}' has SupportsOutcomePull=false; skipping (tenant={TenantId} instance={InstanceId}).",
                descriptor.TypeCode, descriptor.TenantId, descriptor.ExternalSystemInstanceId);
            return;
        }

        var cursor = await LoadOrInitializeCursorAsync(db, descriptor, ct);
        var window = ComputePullWindow(cursor);
        var cfg = new ExternalSystemConfig(
            InstanceId: descriptor.ExternalSystemInstanceId,
            TenantId: descriptor.TenantId,
            ConfigJson: descriptor.ConfigJson);

        _logger.LogInformation(
            "OutcomePullWorker pulling tenant={TenantId} instance={InstanceId} phase={Phase} window=[{Since:o},{Until:o}) kind={Kind}.",
            descriptor.TenantId, descriptor.ExternalSystemInstanceId, descriptor.Phase,
            window.Since, window.Until, window.Kind);

        var fetched = await adapter.FetchOutcomesAsync(cfg, window, ct);

        // Persist each fetched outcome via the writer. The writer is
        // idempotent (§6.11.7) so replay over a partially-advanced cursor
        // is safe; a duplicate returns Deduplicated and the count is
        // reflected in the metric. One bad record does not block the
        // rest of the batch — log + continue, and let the next cycle
        // replay the whole window.
        var inserted = 0;
        var deduped = 0;
        var superseded = 0;
        var unmatched = 0;
        var failed = 0;
        foreach (var dto in fetched)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = MapDtoToRecord(dto, descriptor, _clock.GetUtcNow());
                var outcome = await writer.WriteAsync(record, ct);
                switch (outcome)
                {
                    case OutcomeWriteOutcome.Inserted: inserted++; break;
                    case OutcomeWriteOutcome.Deduplicated: deduped++; break;
                    case OutcomeWriteOutcome.Superseded: superseded++; break;
                    case OutcomeWriteOutcome.NoMatchingCase: unmatched++; break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "OutcomePullWorker writer failed on one record (instance={InstanceId} ref={Ref}); continuing batch.",
                    descriptor.ExternalSystemInstanceId, dto.ReferenceNumber);
            }
        }

        // Cursor advance — only on a clean batch (no per-record failure).
        // A single failed write leaves the cursor at its prior position
        // so the next cycle replays the whole window. The writer's
        // idempotency contract (§6.11.7) makes that safe — the inserts
        // already in the DB will return Deduplicated on replay.
        if (failed == 0)
        {
            await AdvanceCursorAsync(db, descriptor, cursor, window, ct);
            _logger.LogInformation(
                "OutcomePullWorker advanced cursor instance={InstanceId} until={Until:o} inserted={Inserted} deduped={Deduped} superseded={Superseded} unmatched={Unmatched}.",
                descriptor.ExternalSystemInstanceId, window.Until, inserted, deduped, superseded, unmatched);
        }
        else
        {
            // Bump ConsecutiveFailures + leave the window pointer
            // unchanged. The next cycle will re-pull the same range.
            await BumpFailureCounterAsync(db, descriptor, cursor, ct);
            _logger.LogWarning(
                "OutcomePullWorker batch had {Failed} failures (instance={InstanceId}); cursor unchanged so the next cycle replays the window.",
                failed, descriptor.ExternalSystemInstanceId);
        }
    }

    /// <summary>
    /// Advance the cursor on the writer-success path. Upserts the row
    /// (cursor may not yet exist in the DB on first cycle) — RLS
    /// enforces tenant scoping via the worker-scope's
    /// <see cref="ITenantContext"/>.
    /// </summary>
    private async Task AdvanceCursorAsync(
        InspectionDbContext db, PhaseDescriptor descriptor,
        OutcomePullCursor priorCursor, OutcomeWindow window, CancellationToken ct)
    {
        var tracked = await db.OutcomePullCursors
            .FirstOrDefaultAsync(c => c.ExternalSystemInstanceId == descriptor.ExternalSystemInstanceId, ct);

        if (tracked is null)
        {
            tracked = new OutcomePullCursor
            {
                ExternalSystemInstanceId = descriptor.ExternalSystemInstanceId,
                TenantId = descriptor.TenantId,
                LastSuccessfulPullAt = window.Until,
                LastPullWindowUntil = window.Until,
                ConsecutiveFailures = 0
            };
            db.OutcomePullCursors.Add(tracked);
        }
        else
        {
            tracked.LastSuccessfulPullAt = window.Until;
            tracked.LastPullWindowUntil = window.Until;
            tracked.ConsecutiveFailures = 0;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// On a partial-failure batch, bump the failure counter without
    /// advancing the window pointer. Drives the §6.11.12
    /// <c>posthoc_pull_cursor_lag_seconds</c> alarm logic — the next
    /// cycle replays the same window, and the counter only resets on a
    /// clean cycle.
    /// </summary>
    private async Task BumpFailureCounterAsync(
        InspectionDbContext db, PhaseDescriptor descriptor,
        OutcomePullCursor priorCursor, CancellationToken ct)
    {
        var tracked = await db.OutcomePullCursors
            .FirstOrDefaultAsync(c => c.ExternalSystemInstanceId == descriptor.ExternalSystemInstanceId, ct);

        if (tracked is null)
        {
            tracked = new OutcomePullCursor
            {
                ExternalSystemInstanceId = descriptor.ExternalSystemInstanceId,
                TenantId = descriptor.TenantId,
                LastSuccessfulPullAt = priorCursor.LastSuccessfulPullAt,
                LastPullWindowUntil = priorCursor.LastPullWindowUntil,
                ConsecutiveFailures = 1
            };
            db.OutcomePullCursors.Add(tracked);
        }
        else
        {
            tracked.ConsecutiveFailures += 1;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Map a single <see cref="AuthorityDocumentDto"/> from the adapter
    /// to a <see cref="PostHocOutcomeRecord"/> the writer can persist.
    /// Adapter-shaped fields live inside <c>PayloadJson</c>; the
    /// orchestrator-relevant fields (declaration_number, decision_reference,
    /// decided_at, supersedes_decision_reference, container_id) are
    /// extracted from the JSON if present, with safe fallbacks so a
    /// payload that doesn't follow §6.11.5 still produces an attempt at
    /// persistence rather than a hard error.
    /// </summary>
    private static PostHocOutcomeRecord MapDtoToRecord(
        AuthorityDocumentDto dto,
        PhaseDescriptor descriptor,
        DateTimeOffset fallbackDecidedAt)
    {
        var p = ParsePayload(dto.PayloadJson);

        var declarationNumber = p?["declaration_number"]?.GetValue<string>()
            ?? dto.ReferenceNumber;
        var containerNumber = p?["container_id"]?.GetValue<string>()
            ?? p?["container_number"]?.GetValue<string>();
        var decisionReference = p?["decision_reference"]?.GetValue<string>()
            ?? dto.ReferenceNumber;
        var supersedes = p?["supersedes_decision_reference"]?.GetValue<string?>();
        var entryMethod = p?["entry_method"]?.GetValue<string>() ?? "api";

        DateTimeOffset decidedAt = fallbackDecidedAt;
        var decidedAtRaw = p?["decided_at"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(decidedAtRaw)
            && DateTimeOffset.TryParse(decidedAtRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            decidedAt = parsed;
        }

        return new PostHocOutcomeRecord(
            TenantId: descriptor.TenantId,
            ExternalSystemInstanceId: descriptor.ExternalSystemInstanceId,
            AuthorityCode: descriptor.TypeCode,
            DeclarationNumber: declarationNumber,
            ContainerNumber: containerNumber,
            DecidedAt: decidedAt,
            DecisionReference: decisionReference,
            SupersedesDecisionReference: supersedes,
            PayloadJson: dto.PayloadJson,
            Phase: descriptor.Phase,
            EntryMethod: entryMethod);
    }

    private static System.Text.Json.Nodes.JsonObject? ParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try { return System.Text.Json.Nodes.JsonNode.Parse(payloadJson) as System.Text.Json.Nodes.JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Load the cursor for this instance, or initialize a fresh one if
    /// it doesn't exist. Initialization seeds <c>LastPullWindowUntil</c>
    /// at <c>now() - 24h</c> so the first cycle picks up the prior 24
    /// hours rather than racing forward; this matches the
    /// <see cref="OutcomeIngestionOptions.WindowOverlap"/> default.
    /// </summary>
    /// <remarks>
    /// Commit 1 only reads — it does not yet persist a fresh cursor
    /// row. Commit 2 wires the upsert + cursor-advance path.
    /// </remarks>
    private async Task<OutcomePullCursor> LoadOrInitializeCursorAsync(
        InspectionDbContext db, PhaseDescriptor descriptor, CancellationToken ct)
    {
        var existing = await db.Set<OutcomePullCursor>()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.ExternalSystemInstanceId == descriptor.ExternalSystemInstanceId, ct);

        if (existing is not null)
            return existing;

        var now = _clock.GetUtcNow();
        return new OutcomePullCursor
        {
            ExternalSystemInstanceId = descriptor.ExternalSystemInstanceId,
            TenantId = descriptor.TenantId,
            LastSuccessfulPullAt = now - _options.Value.WindowOverlap,
            LastPullWindowUntil = now - _options.Value.WindowOverlap,
            ConsecutiveFailures = 0
        };
    }

    /// <summary>
    /// Compute the next pull window from the cursor. Per §6.11.8:
    /// <c>since = LastPullWindowUntil - WindowOverlap</c>;
    /// <c>until = now() - SkewBuffer</c>.
    /// </summary>
    internal OutcomeWindow ComputePullWindow(OutcomePullCursor cursor)
    {
        var opts = _options.Value;
        var until = _clock.GetUtcNow() - opts.SkewBuffer;
        var since = cursor.LastPullWindowUntil - opts.WindowOverlap;
        var kind = ParseWindowKind(opts.DefaultWindowKind);
        return new OutcomeWindow(since, until, kind);
    }

    private static OutcomeWindowKind ParseWindowKind(string raw)
        => Enum.TryParse<OutcomeWindowKind>(raw, ignoreCase: true, out var k)
           ? k
           : OutcomeWindowKind.DecidedAt;

    /// <summary>Lightweight projection over <see cref="PostHocRolloutPhase"/>+<see cref="ExternalSystemInstance"/> joined data.</summary>
    private sealed record PhaseDescriptor(
        long TenantId,
        Guid ExternalSystemInstanceId,
        string TypeCode,
        string ConfigJson,
        PostHocRolloutPhaseValue Phase);
}
