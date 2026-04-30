using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Manual-entry path for post-hoc outcomes (§6.11.9). Operators on the
/// admin surface select an <see cref="InspectionCase"/> and fill in a
/// payload mirroring §6.11.5; this service builds the
/// <see cref="PostHocOutcomeRecord"/>, picks (or seeds) the per-tenant
/// manual-entry pseudo-instance, and routes the row through the same
/// <see cref="IPostHocOutcomeWriter"/> as the worker.
///
/// <para>
/// Single source of truth for entry — both the worker and this service
/// land in <c>authority_documents</c> via the writer, so downstream
/// consumers see one canonical persistence shape regardless of whether
/// the row came from the API or a clipboard. Distinguishable provenance
/// via <c>payload.entry_method = "manual"</c>.
/// </para>
///
/// <para>
/// Per §6.11.9 the manual-entry pseudo-instance has no credentials
/// (<c>payload.mode = "manual"</c>); this service auto-creates one per
/// tenant on first use rather than requiring an onboarding step. The
/// type code is <c>"manual-entry"</c> by convention.
/// </para>
/// </summary>
public sealed class PostHocOutcomeManualEntryService
{
    /// <summary>Type code stamped on the manual-entry pseudo-instance.</summary>
    public const string ManualEntryTypeCode = "manual-entry";

    /// <summary>Display name on the auto-seeded pseudo-instance.</summary>
    public const string ManualEntryDisplayName = "Manual entry (operator)";

    private readonly InspectionDbContext _db;
    private readonly IPostHocOutcomeWriter _writer;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly AuthenticationStateProvider? _auth;
    private readonly ILogger<PostHocOutcomeManualEntryService> _logger;
    private readonly TimeProvider _clock;

    public PostHocOutcomeManualEntryService(
        InspectionDbContext db,
        IPostHocOutcomeWriter writer,
        IEventPublisher events,
        ITenantContext tenant,
        ILogger<PostHocOutcomeManualEntryService> logger,
        AuthenticationStateProvider? auth = null,
        TimeProvider? clock = null)
    {
        _db = db;
        _writer = writer;
        _events = events;
        _tenant = tenant;
        _auth = auth;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Persist a manual-entry post-hoc outcome. Resolves the operator
    /// from the current Razor auth state; idempotent at the writer
    /// layer — a double-submit on the same form returns
    /// <see cref="OutcomeWriteOutcome.Deduplicated"/>.
    /// </summary>
    /// <param name="caseId">
    /// The case the operator selected. Spec calls for declaration- /
    /// container-number selection on the form which is then resolved to
    /// a case id; this method takes the resolved id directly.
    /// </param>
    /// <param name="form">Form fields mirroring §6.11.5.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<OutcomeWriteOutcome> RecordAsync(
        Guid caseId, ManualEntryForm form, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved — UseNickErpTenancy() must run before this admin action.");
        }
        var tenantId = _tenant.TenantId;

        var operatorUserId = await ResolveOperatorAsync();

        // Validate the case exists + belongs to this tenant. RLS would
        // already block a cross-tenant id, but a clear exception beats a
        // silent NoMatchingCase from the writer.
        var matchedCase = await _db.Cases
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == caseId, ct)
            ?? throw new InvalidOperationException(
                $"Case {caseId} not found (or not visible under tenant {tenantId}).");

        // Resolve / seed the per-tenant manual-entry pseudo-instance.
        var pseudoInstance = await EnsureManualPseudoInstanceAsync(tenantId, ct);

        // Build the §6.11.5 payload shape from form fields. Operators
        // can hand-edit any field; the typed ManualEntryForm captures
        // the locked acceptance set and falls back to free-form notes
        // for anything else.
        var payloadJson = BuildPayloadJson(form, operatorUserId);

        var declaration = string.IsNullOrEmpty(form.DeclarationNumber)
            ? matchedCase.SubjectIdentifier
            : form.DeclarationNumber!;

        var record = new PostHocOutcomeRecord(
            TenantId: tenantId,
            ExternalSystemInstanceId: pseudoInstance.Id,
            AuthorityCode: ManualEntryTypeCode,
            DeclarationNumber: declaration,
            ContainerNumber: form.ContainerNumber,
            DecidedAt: form.DecidedAt ?? _clock.GetUtcNow(),
            DecisionReference: form.DecisionReference,
            SupersedesDecisionReference: form.SupersedesDecisionReference,
            PayloadJson: payloadJson,
            Phase: PostHocRolloutPhaseValue.DevEvalManualOnly,
            EntryMethod: "manual");

        var outcome = await _writer.WriteAsync(record, ct);

        // Audit emit so the operator action is captured even when the
        // writer dedups (helps the §6.11.10 read-audit posture).
        await EmitAsync(
            tenantId, operatorUserId,
            eventType: "nickerp.inspection.posthoc_outcome_manual_entered",
            entityType: nameof(AuthorityDocument),
            entityId: matchedCase.Id.ToString(),
            payload: new
            {
                caseId = matchedCase.Id,
                pseudoInstanceId = pseudoInstance.Id,
                outcome = outcome.ToString(),
                decisionReference = form.DecisionReference,
                supersedes = form.SupersedesDecisionReference
            },
            ct);

        _logger.LogInformation(
            "Manual post-hoc entry caseId={CaseId} outcome={Outcome} operator={Operator}.",
            matchedCase.Id, outcome, operatorUserId);

        return outcome;
    }

    /// <summary>
    /// Find or create the per-tenant manual-entry pseudo-instance. One
    /// row per tenant (Scope=PerLocation but bound to no specific
    /// location); typeCode <c>"manual-entry"</c>.
    /// </summary>
    private async Task<ExternalSystemInstance> EnsureManualPseudoInstanceAsync(
        long tenantId, CancellationToken ct)
    {
        var existing = await _db.ExternalSystemInstances
            .FirstOrDefaultAsync(e => e.TypeCode == ManualEntryTypeCode, ct);
        if (existing is not null) return existing;

        var fresh = new ExternalSystemInstance
        {
            Id = Guid.NewGuid(),
            TypeCode = ManualEntryTypeCode,
            DisplayName = ManualEntryDisplayName,
            Description = "Auto-seeded manual-entry pseudo-instance for §6.11.9 operator-driven post-hoc outcomes.",
            Scope = ExternalSystemBindingScope.Shared,
            ConfigJson = "{\"mode\":\"manual\"}",
            IsActive = true,
            CreatedAt = _clock.GetUtcNow(),
            TenantId = tenantId
        };
        _db.ExternalSystemInstances.Add(fresh);
        await _db.SaveChangesAsync(ct);
        return fresh;
    }

    private static string BuildPayloadJson(ManualEntryForm form, Guid? operatorUserId)
    {
        var obj = new JsonObject
        {
            ["$schema"] = "manual.posthoc-outcome.v1",
            ["declaration_number"] = form.DeclarationNumber,
            ["container_id"] = form.ContainerNumber,
            ["outcome"] = form.Outcome,
            ["seized_count"] = form.SeizedCount,
            ["decided_at"] = form.DecidedAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["decided_by_officer_id"] = form.DecidedByOfficerId,
            ["decision_reference"] = form.DecisionReference,
            ["supersedes_decision_reference"] = form.SupersedesDecisionReference,
            ["entry_method"] = "manual",
            ["entered_by_user_id"] = operatorUserId?.ToString(),
            ["operator_notes"] = form.OperatorNotes
        };
        return obj.ToJsonString();
    }

    private async Task<Guid?> ResolveOperatorAsync()
    {
        if (_auth is null) return null;
        try
        {
            var state = await _auth.GetAuthenticationStateAsync();
            var idClaim = state.User?.FindFirst("nickerp:id")?.Value;
            if (Guid.TryParse(idClaim, out var g)) return g;
        }
        catch (InvalidOperationException)
        {
            // Outside a Razor scope (tests / direct service invocation) — null is fine.
        }
        return null;
    }

    private async Task EmitAsync(
        long tenantId, Guid? actor,
        string eventType, string entityType, string entityId, object payload,
        CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(tenantId, eventType, entityType, entityId, _clock.GetUtcNow());
            var evt = DomainEvent.Create(tenantId, actor, null, eventType, entityType, entityId, json, key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            // Audit emission must not break user-facing workflows.
            _logger.LogWarning(ex,
                "Failed to emit DomainEvent {EventType} for {EntityType} {EntityId}",
                eventType, entityType, entityId);
        }
    }
}

/// <summary>
/// Form fields mirroring the §6.11.5 ICUMS Ghana payload shape. Used by
/// both the Razor admin page and direct service callers (tests). Most
/// fields are nullable so the form can be submitted partial; only the
/// outcome string and decision_reference are load-bearing.
/// </summary>
public sealed class ManualEntryForm
{
    /// <summary>Authority-side declaration / BOE number.</summary>
    public string? DeclarationNumber { get; set; }

    /// <summary>Container number (e.g. <c>MSCU1234567</c>) — optional.</summary>
    public string? ContainerNumber { get; set; }

    /// <summary>Outcome value: <c>Cleared</c> | <c>Seized</c> | <c>ReleasedWithDuty</c> | <c>ReExported</c> | <c>Pending</c>.</summary>
    public string Outcome { get; set; } = "Cleared";

    /// <summary>Number of seized items (0 for Cleared / Pending).</summary>
    public int SeizedCount { get; set; }

    /// <summary>When the authority rendered the decision. Defaults to <c>now()</c> if null.</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Officer id at the authority, if known.</summary>
    public string? DecidedByOfficerId { get; set; }

    /// <summary>Authority-side reference (seizure number, clearance ref, etc.) — REQUIRED.</summary>
    public string DecisionReference { get; set; } = string.Empty;

    /// <summary>If this is a correction to a prior outcome, the prior decision_reference.</summary>
    public string? SupersedesDecisionReference { get; set; }

    /// <summary>Operator-supplied free-form notes. Goes into payload.operator_notes.</summary>
    public string? OperatorNotes { get; set; }
}
