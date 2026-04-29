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

        var now = DateTimeOffset.UtcNow;
        row.Status = ScannerThresholdProfileStatus.Shadow;
        row.ApprovedByUserId = actorUserId;
        row.ApprovedAt = now;
        row.ShadowStartedAt = now;
        row.UpdatedAt = now;

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

        _logger.LogInformation(
            "Threshold profile {ProfileId} (scanner {ScannerId} v{Version}) approved by {UserId}; shadow started at {Now:o}",
            row.Id, row.ScannerDeviceInstanceId, row.Version, actorUserId, now);
    }

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
