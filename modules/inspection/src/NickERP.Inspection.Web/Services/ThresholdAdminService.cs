using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Admin actions for <see cref="ScannerThresholdProfile"/> rows (§6.5.3
/// propose → approve → shadow → activate flow).
///
/// <para>
/// <see cref="ApproveAsync"/> moves a <see cref="ScannerThresholdProfileStatus.Proposed"/>
/// row into <see cref="ScannerThresholdProfileStatus.Shadow"/>, stamping
/// the approver and <c>shadow_started_at = now()</c>; then emits
/// <c>nickerp.inspection.scanner_threshold_proposal_approved</c> for any
/// downstream listener (the actual shadow runner is out of scope for
/// this team — Phase 1 manual-tune UI is what this powers; Phase 2/3
/// auto-tune ride the same path).
/// </para>
///
/// <para>
/// <see cref="RejectAsync"/> moves a Proposed row into
/// <see cref="ScannerThresholdProfileStatus.Rejected"/>, stamping
/// <c>proposal_rationale.rejected_by_user_id</c> and
/// <c>rejected_at</c> directly into the rationale jsonb so an audit
/// trail of who-rejected-what survives without a schema bump.
/// </para>
/// </summary>
public sealed class ThresholdAdminService
{
    private readonly InspectionDbContext _db;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly AuthenticationStateProvider _auth;
    private readonly ILogger<ThresholdAdminService> _logger;

    public ThresholdAdminService(
        InspectionDbContext db,
        IEventPublisher events,
        ITenantContext tenant,
        AuthenticationStateProvider auth,
        ILogger<ThresholdAdminService> logger)
    {
        _db = db;
        _events = events;
        _tenant = tenant;
        _auth = auth;
        _logger = logger;
    }

    /// <summary>
    /// Approve a Proposed row → mark Shadow + stamp approver + start the
    /// shadow window. Per §6.5.3 the shadow runner monitors the row;
    /// after the 24 h window with shadow gate pass, the row is promoted
    /// to Active by a separate component (out of scope for Team A).
    ///
    /// <para>
    /// Sprint 46 / Phase B — diff the new <see cref="ScannerThresholdProfile.ValuesJson"/>
    /// against the prior Active row's ValuesJson for the same scanner
    /// and emit one <see cref="ThresholdProfileHistory"/> row per differing
    /// (model, class) pair. Audit event
    /// <c>nickerp.inspection.threshold_changed</c> fires alongside,
    /// carrying the same diff plus the actor and rationale. Best-effort
    /// emission (warning + continue on failure; matches Sprint 28
    /// pattern). The append-only history table mirrors the audit posture
    /// of <c>audit.events</c> for the doc-analysis Table 21
    /// "reversibility" requirement.
    /// </para>
    /// </summary>
    public async Task ApproveAsync(Guid profileId, CancellationToken ct = default)
    {
        var (actorUserId, tenantId) = await CurrentActorAsync();

        var row = await _db.ScannerThresholdProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId, ct)
            ?? throw new InvalidOperationException($"Threshold profile {profileId} not found.");

        if (row.Status != ScannerThresholdProfileStatus.Proposed)
        {
            throw new InvalidOperationException(
                $"Cannot approve profile {profileId} in status {row.Status}; only Proposed rows are approvable.");
        }

        // Look up the prior Active row for the same scanner so we can
        // diff its ValuesJson against the proposal. Null on bootstrap /
        // first-proposal — the diff records OldThreshold = null in that
        // case, matching the entity comment's documented contract.
        var priorActive = await _db.ScannerThresholdProfiles
            .AsNoTracking()
            .Where(p => p.ScannerDeviceInstanceId == row.ScannerDeviceInstanceId
                     && p.Status == ScannerThresholdProfileStatus.Active
                     && p.Id != row.Id)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        var rationale = ExtractReason(row.ProposalRationaleJson);
        var diffs = DiffThresholdValues(priorActive?.ValuesJson, row.ValuesJson);

        var now = DateTimeOffset.UtcNow;
        row.Status = ScannerThresholdProfileStatus.Shadow;
        row.ApprovedByUserId = actorUserId;
        row.ApprovedAt = now;
        row.ShadowStartedAt = now;
        row.UpdatedAt = now;

        // Sprint 46 / Phase B — write one ThresholdProfileHistory row per
        // (model, class) delta. Append-only; never updated or deleted.
        foreach (var diff in diffs)
        {
            _db.ThresholdProfileHistory.Add(new ThresholdProfileHistory
            {
                Id = Guid.NewGuid(),
                ScannerDeviceInstanceId = row.ScannerDeviceInstanceId,
                ModelId = diff.ModelId,
                ClassId = diff.ClassId,
                OldThreshold = diff.OldThreshold,
                NewThreshold = diff.NewThreshold,
                ChangedAt = now,
                ChangedByUserId = actorUserId,
                Reason = rationale,
                TenantId = tenantId
            });
        }

        await _db.SaveChangesAsync(ct);

        // DomainEvent — see §6.5.9 Q-H4 (recommendation: every state change emits an event).
        await EmitAsync(
            tenantId, actorUserId,
            "nickerp.inspection.scanner_threshold_proposal_approved",
            "ScannerThresholdProfile", row.Id.ToString(),
            new
            {
                row.Id,
                row.ScannerDeviceInstanceId,
                row.Version,
                approvedByUserId = actorUserId,
                approvedAt = now,
                shadowStartedAt = now
            }, ct);

        // Sprint 46 / Phase B — emit one threshold_changed audit event
        // per (model, class) delta. Carries the same shape as the history
        // rows so downstream consumers (SIEM forwarders, the upcoming
        // /admin/thresholds/history page) can react without a DB
        // round-trip. Best-effort: EmitAsync swallows failures with a
        // warning so user-facing workflows aren't blocked by audit-pipe
        // hiccups.
        foreach (var diff in diffs)
        {
            await EmitAsync(
                tenantId, actorUserId,
                "nickerp.inspection.threshold_changed",
                "ScannerThresholdProfile", row.Id.ToString(),
                new
                {
                    tenantId,
                    scannerDeviceInstanceId = row.ScannerDeviceInstanceId,
                    modelId = diff.ModelId,
                    classId = diff.ClassId,
                    oldThreshold = diff.OldThreshold,
                    newThreshold = diff.NewThreshold,
                    userId = actorUserId,
                    reason = rationale
                }, ct);
        }

        _logger.LogInformation(
            "Threshold profile {ProfileId} (scanner {ScannerId} v{Version}) approved by {UserId}; shadow started at {Now:o}; {DiffCount} threshold delta(s) recorded",
            row.Id, row.ScannerDeviceInstanceId, row.Version, actorUserId, now, diffs.Count);
    }

    /// <summary>
    /// Sprint 46 / Phase B — compare two threshold-values JSON blobs
    /// keyed by <c>{model}.{class}</c> (matching the
    /// <c>ScannerDeviceType.threshold_schema</c> shape: top-level model
    /// keys, nested class keys, leaf double values). Returns one diff
    /// per pair where (a) the new value exists and (b) it differs from
    /// the old (or the old is missing). Diffs that vanish in the new
    /// JSON are NOT emitted — Sprint 41's history shape only records
    /// what was set, not what was removed (the row's overall ValuesJson
    /// snapshot is on ScannerThresholdProfile).
    ///
    /// <para>
    /// Tolerant of malformed JSON and exotic shapes: anything we can't
    /// flatten to a (model, class, double) tuple is silently skipped
    /// rather than throwing, so a bootstrap row with an empty / non-
    /// object ValuesJson doesn't crash approve. Returns an empty list
    /// when nothing changed.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ThresholdDiff> DiffThresholdValues(
        string? oldJson,
        string? newJson)
    {
        var oldFlat = FlattenThresholdValues(oldJson);
        var newFlat = FlattenThresholdValues(newJson);

        var diffs = new List<ThresholdDiff>();
        foreach (var (key, newValue) in newFlat)
        {
            oldFlat.TryGetValue(key, out var oldValue);
            // No prior key = always a change (OldThreshold null).
            // Same value as before = no change.
            if (oldFlat.ContainsKey(key) && oldValue == newValue) continue;

            var (modelId, classId) = SplitKey(key);
            diffs.Add(new ThresholdDiff(
                ModelId: modelId,
                ClassId: classId,
                OldThreshold: oldFlat.ContainsKey(key) ? oldValue : null,
                NewThreshold: newValue));
        }
        return diffs;
    }

    /// <summary>
    /// Flatten a threshold-values JSON blob to a (modelId.classId →
    /// threshold-double) dictionary. Tolerant: returns an empty dict on
    /// null / whitespace / parse error / non-object root.
    /// </summary>
    private static Dictionary<string, double> FlattenThresholdValues(string? json)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

            foreach (var modelProp in doc.RootElement.EnumerateObject())
            {
                if (modelProp.Value.ValueKind == JsonValueKind.Number)
                {
                    // Top-level scalar — treat as model-only (no class).
                    if (modelProp.Value.TryGetDouble(out var topLevel))
                    {
                        result[modelProp.Name + "."] = topLevel;
                    }
                    continue;
                }
                if (modelProp.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var classProp in modelProp.Value.EnumerateObject())
                {
                    if (classProp.Value.ValueKind != JsonValueKind.Number) continue;
                    if (!classProp.Value.TryGetDouble(out var v)) continue;
                    result[modelProp.Name + "." + classProp.Name] = v;
                }
            }
        }
        return result;
    }

    private static (string ModelId, string ClassId) SplitKey(string key)
    {
        var idx = key.IndexOf('.');
        if (idx < 0) return (key, string.Empty);
        return (key[..idx], key[(idx + 1)..]);
    }

    /// <summary>
    /// Pull a free-form <c>note</c> / <c>reason</c> / <c>rationale</c>
    /// string out of the proposal-rationale jsonb so the
    /// <see cref="ThresholdProfileHistory.Reason"/> column carries the
    /// human-supplied explanation without serialising the whole blob.
    /// Returns null on missing / non-string / malformed.
    /// </summary>
    private static string? ExtractReason(string? rationaleJson)
    {
        if (string.IsNullOrWhiteSpace(rationaleJson)) return null;
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(rationaleJson);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "note", "reason", "rationale" })
            {
                if (doc.RootElement.TryGetProperty(key, out var p)
                    && p.ValueKind == JsonValueKind.String)
                {
                    return p.GetString();
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Sprint 46 / Phase B — per-(model, class) delta produced by the
    /// approve diff. <see cref="OldThreshold"/> is null on bootstrap or
    /// any approval where the prior Active row didn't carry this
    /// (model, class) pair.
    /// </summary>
    internal sealed record ThresholdDiff(
        string ModelId,
        string ClassId,
        double? OldThreshold,
        double NewThreshold);

    /// <summary>
    /// Reject a Proposed row. Stamps the rejector + timestamp into the
    /// rationale jsonb so the audit trail survives without a column add;
    /// flips Status → Rejected.
    /// </summary>
    public async Task RejectAsync(Guid profileId, CancellationToken ct = default)
    {
        var (actorUserId, tenantId) = await CurrentActorAsync();

        var row = await _db.ScannerThresholdProfiles
            .FirstOrDefaultAsync(p => p.Id == profileId, ct)
            ?? throw new InvalidOperationException($"Threshold profile {profileId} not found.");

        if (row.Status != ScannerThresholdProfileStatus.Proposed)
        {
            throw new InvalidOperationException(
                $"Cannot reject profile {profileId} in status {row.Status}; only Proposed rows are rejectable.");
        }

        var now = DateTimeOffset.UtcNow;

        // Merge the rejection metadata into the existing rationale jsonb,
        // preserving every original key. JsonNode is happy with either
        // an object or null root — fall back to {} if we got something
        // exotic (string, array) so the patch never silently drops the
        // history.
        JsonObject rationale;
        try
        {
            rationale = JsonNode.Parse(row.ProposalRationaleJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            rationale = new JsonObject();
        }
        rationale["rejected_by_user_id"] = actorUserId?.ToString();
        rationale["rejected_at"] = now.ToString("o");
        row.ProposalRationaleJson = rationale.ToJsonString();
        row.Status = ScannerThresholdProfileStatus.Rejected;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        await EmitAsync(
            tenantId, actorUserId,
            "nickerp.inspection.scanner_threshold_proposal_rejected",
            "ScannerThresholdProfile", row.Id.ToString(),
            new
            {
                row.Id,
                row.ScannerDeviceInstanceId,
                row.Version,
                rejectedByUserId = actorUserId,
                rejectedAt = now
            }, ct);

        _logger.LogInformation(
            "Threshold profile {ProfileId} (scanner {ScannerId} v{Version}) rejected by {UserId} at {Now:o}",
            row.Id, row.ScannerDeviceInstanceId, row.Version, actorUserId, now);
    }

    private async Task<(Guid? UserId, long TenantId)> CurrentActorAsync()
    {
        Guid? id = null;
        try
        {
            var state = await _auth.GetAuthenticationStateAsync();
            var idClaim = state.User?.FindFirst("nickerp:id")?.Value;
            if (Guid.TryParse(idClaim, out var g)) id = g;
        }
        catch (InvalidOperationException)
        {
            // Outside a Razor scope (workers/tests) — actor null is fine.
        }

        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved — UseNickErpTenancy() must run before this admin action.");
        }
        return (id, _tenant.TenantId);
    }

    private async Task EmitAsync(
        long tenantId, Guid? actor,
        string eventType, string entityType, string entityId, object payload,
        CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(tenantId, eventType, entityType, entityId, DateTimeOffset.UtcNow);
            var evt = DomainEvent.Create(tenantId, actor, null, eventType, entityType, entityId, json, key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            // Audit emission must not break user-facing workflows.
            _logger.LogWarning(ex, "Failed to emit DomainEvent {EventType} for {EntityType} {EntityId}", eventType, entityType, entityId);
        }
    }
}
