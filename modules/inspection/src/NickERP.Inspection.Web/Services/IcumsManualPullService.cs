using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Plugins;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 22 / B2.3 — admin-triggered manual re-fetch of post-hoc
/// outcomes for one
/// <see cref="ExternalSystemInstance"/> across an explicit time window.
/// Mirrors the per-instance fetch shape the
/// <see cref="OutcomePullWorker"/> uses but drives it on operator
/// command rather than the periodic cycle.
///
/// <para>
/// **Why this lives in <c>Inspection.Web</c>.</b> The manual-pull path
/// needs an <see cref="IPluginRegistry"/> to resolve the
/// <see cref="IInboundOutcomeAdapter"/>; the registry is wired in
/// <c>Program.cs</c> only. The Application layer doesn't have plugin
/// access by design (no plugin contract project ref outside the host).
/// </para>
///
/// <para>
/// **Cursor advance.</b> Manual pulls do NOT advance the
/// <see cref="OutcomePullCursor"/>. The cursor only tracks the
/// scheduled-pull state machine; a one-off operator pull might cover a
/// custom window unrelated to where the cursor is, so it would be
/// unsafe to mutate the cursor. The writer's idempotency dedup
/// guarantees a manual pull over an already-pulled window is harmless.
/// </para>
/// </summary>
public sealed class IcumsManualPullService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IPluginRegistry _plugins;
    private readonly IPostHocOutcomeWriter _writer;
    private readonly IServiceProvider _services;
    private readonly ILogger<IcumsManualPullService> _logger;

    public IcumsManualPullService(
        InspectionDbContext db,
        ITenantContext tenant,
        IPluginRegistry plugins,
        IPostHocOutcomeWriter writer,
        IServiceProvider services,
        ILogger<IcumsManualPullService> logger)
    {
        _db = db;
        _tenant = tenant;
        _plugins = plugins;
        _writer = writer;
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Trigger a one-shot pull for an
    /// <see cref="ExternalSystemInstance"/>. The window is operator-
    /// supplied (UTC). The window-kind defaults to
    /// <see cref="OutcomeWindowKind.LastModifiedAt"/> — the most
    /// permissive choice for a manual reconciliation pull.
    /// </summary>
    public async Task<ManualPullResult> PullAsync(
        Guid externalSystemInstanceId,
        DateTimeOffset sinceUtc,
        DateTimeOffset untilUtc,
        OutcomeWindowKind windowKind = OutcomeWindowKind.LastModifiedAt,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();

        if (untilUtc <= sinceUtc)
        {
            return ManualPullResult.Failure(
                "Until must be strictly after Since. Adjust the window.");
        }

        var esi = await _db.ExternalSystemInstances.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == externalSystemInstanceId, ct)
            .ConfigureAwait(false);
        if (esi is null)
        {
            return ManualPullResult.Failure(
                $"External system instance {externalSystemInstanceId} not found in this tenant.");
        }

        IInboundOutcomeAdapter adapter;
        try
        {
            adapter = _plugins.Resolve<IInboundOutcomeAdapter>(
                "inspection", esi.TypeCode, _services);
        }
        catch (KeyNotFoundException)
        {
            return ManualPullResult.Failure(
                $"No plugin registered for typeCode '{esi.TypeCode}'. Check plugin folder + restart.");
        }
        catch (InvalidOperationException ex)
        {
            return ManualPullResult.Failure(
                $"Plugin '{esi.TypeCode}' does not implement IInboundOutcomeAdapter: {ex.Message}");
        }

        if (!adapter.Capabilities.SupportsOutcomePull)
        {
            return ManualPullResult.Failure(
                $"Adapter '{esi.TypeCode}' has SupportsOutcomePull=false; manual pull not supported.");
        }

        var window = new OutcomeWindow(sinceUtc, untilUtc, windowKind);
        var cfg = new ExternalSystemConfig(
            InstanceId: externalSystemInstanceId,
            TenantId: _tenant.TenantId,
            ConfigJson: esi.ConfigJson);

        _logger.LogInformation(
            "ManualPull tenant={TenantId} instance={InstanceId} window=[{Since:o},{Until:o}) kind={Kind}.",
            _tenant.TenantId, externalSystemInstanceId, sinceUtc, untilUtc, windowKind);

        IReadOnlyList<AuthorityDocumentDto> fetched;
        try
        {
            fetched = await adapter.FetchOutcomesAsync(cfg, window, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ManualPull adapter call failed (instance={InstanceId}).",
                externalSystemInstanceId);
            return ManualPullResult.Failure($"Adapter call failed: {ex.Message}");
        }

        // Persist via the same writer the periodic worker uses. Cursor
        // is intentionally NOT advanced (see class summary).
        var inserted = 0;
        var deduped = 0;
        var superseded = 0;
        var unmatched = 0;
        var failed = 0;
        var nowUtc = DateTimeOffset.UtcNow;
        // Pick the phase the manual pull writes under — we read the
        // current phase row when it exists; fall back to PrimaryPlus5PctAudit
        // (full path enabled) for new authorities since a manual pull
        // by definition is a deliberate operator action.
        var phase = await ResolvePhaseAsync(externalSystemInstanceId, ct).ConfigureAwait(false);

        foreach (var dto in fetched)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = ManualPullRecordMapper.Map(
                    dto, externalSystemInstanceId, _tenant.TenantId, esi.TypeCode, nowUtc, phase);
                var outcome = await _writer.WriteAsync(record, ct).ConfigureAwait(false);
                switch (outcome)
                {
                    case OutcomeWriteOutcome.Inserted: inserted++; break;
                    case OutcomeWriteOutcome.Deduplicated: deduped++; break;
                    case OutcomeWriteOutcome.Superseded: superseded++; break;
                    case OutcomeWriteOutcome.NoMatchingCase: unmatched++; break;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex,
                    "ManualPull writer failed on one record (instance={InstanceId} ref={Ref}); continuing batch.",
                    externalSystemInstanceId, dto.ReferenceNumber);
            }
        }

        return new ManualPullResult(
            Success: true,
            Notice: $"Pulled {fetched.Count} record(s); inserted={inserted}, deduped={deduped}, "
                  + $"superseded={superseded}, unmatched={unmatched}, failed={failed}.",
            Fetched: fetched.Count,
            Inserted: inserted,
            Deduplicated: deduped,
            Superseded: superseded,
            Unmatched: unmatched,
            Failed: failed);
    }

    private async Task<PostHocRolloutPhaseValue> ResolvePhaseAsync(
        Guid externalSystemInstanceId, CancellationToken ct)
    {
        var phase = await _db.PostHocRolloutPhases.AsNoTracking()
            .Where(p => p.ExternalSystemInstanceId == externalSystemInstanceId)
            .Select(p => (PostHocRolloutPhaseValue?)p.CurrentPhase)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return phase ?? PostHocRolloutPhaseValue.PrimaryPlus5PctAudit;
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; IcumsManualPullService must run inside a tenant-aware request scope.");
    }
}

/// <summary>
/// Tiny helper that turns an adapter <see cref="AuthorityDocumentDto"/>
/// into a <see cref="PostHocOutcomeRecord"/> for the writer. Kept as a
/// static for testability — the periodic
/// <see cref="OutcomePullWorker"/> has its own equivalent (private
/// there); this mapper carries the manual-pull-specific defaults
/// (<c>EntryMethod = "manual_pull"</c>) and the explicit phase
/// (<see cref="PostHocRolloutPhaseValue.PrimaryPlus5PctAudit"/> as a
/// reasonable default — manual pulls are operator-driven, not phase
/// gated).
/// </summary>
internal static class ManualPullRecordMapper
{
    public static PostHocOutcomeRecord Map(
        AuthorityDocumentDto dto, Guid instanceId, long tenantId, string typeCode,
        DateTimeOffset fallbackDecidedAt, PostHocRolloutPhaseValue phase)
    {
        var p = ParsePayload(dto.PayloadJson);
        var declarationNumber = p?["declaration_number"]?.GetValue<string>() ?? dto.ReferenceNumber;
        var containerNumber = p?["container_id"]?.GetValue<string>()
            ?? p?["container_number"]?.GetValue<string>();
        var decisionReference = p?["decision_reference"]?.GetValue<string>() ?? dto.ReferenceNumber;
        var supersedes = p?["supersedes_decision_reference"]?.GetValue<string?>();

        DateTimeOffset decidedAt = fallbackDecidedAt;
        var decidedAtRaw = p?["decided_at"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(decidedAtRaw)
            && DateTimeOffset.TryParse(decidedAtRaw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            decidedAt = parsed;
        }

        return new PostHocOutcomeRecord(
            TenantId: tenantId,
            ExternalSystemInstanceId: instanceId,
            AuthorityCode: typeCode,
            DeclarationNumber: declarationNumber,
            ContainerNumber: containerNumber,
            DecidedAt: decidedAt,
            DecisionReference: decisionReference,
            SupersedesDecisionReference: supersedes,
            PayloadJson: dto.PayloadJson,
            Phase: phase,
            EntryMethod: "manual_pull");
    }

    private static System.Text.Json.Nodes.JsonObject? ParsePayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try { return System.Text.Json.Nodes.JsonNode.Parse(payloadJson) as System.Text.Json.Nodes.JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>Outcome of a manual-pull call.</summary>
public sealed record ManualPullResult(
    bool Success,
    string Notice,
    int Fetched,
    int Inserted,
    int Deduplicated,
    int Superseded,
    int Unmatched,
    int Failed)
{
    public static ManualPullResult Failure(string notice) =>
        new(false, notice, 0, 0, 0, 0, 0, 0);
}
