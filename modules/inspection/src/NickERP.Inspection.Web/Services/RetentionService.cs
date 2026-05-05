using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Application.Retention;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Retention;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Features;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 44 / Phase A — admin-side service backing the
/// <c>/admin/retention</c> Blazor pages and the
/// <see cref="RetentionEnforcerWorker"/>'s policy lookup.
///
/// <para>
/// Owns the read + write paths for Sprint 39's retention posture
/// (<see cref="InspectionCase.RetentionClass"/> +
/// <see cref="InspectionCase.LegalHold"/>): admin reclassifies a case
/// (<see cref="SetRetentionClassAsync"/>); admin applies / releases a
/// legal hold (<see cref="ApplyLegalHoldAsync"/> /
/// <see cref="ReleaseLegalHoldAsync"/>); admin lists cases under hold or
/// by class (<see cref="ListLegalHoldsAsync"/> /
/// <see cref="ListByRetentionClassAsync"/>). Reading the resolved
/// <see cref="RetentionPolicy"/> for one class
/// (<see cref="GetRetentionPolicyAsync"/>) consults
/// <see cref="ITenantSettingsService"/> for per-tenant overrides
/// (<c>inspection.retention.standard_days</c>,
/// <c>inspection.retention.extended_days</c>,
/// <c>inspection.retention.enforcement_days</c>) and falls back to the
/// hard-coded defaults in <see cref="RetentionPolicyDefaults"/>.
/// </para>
///
/// <para>
/// <b>Cascading legal-hold.</b> Apply / release flow flips the bool on
/// the case AND every <see cref="ScanArtifact"/> under the case in one
/// transaction. The audit event payload carries
/// <c>{caseId, artifactCount, reason, userId}</c> so the audit trail
/// captures the cascade scope. Per Sprint 39 design, hold-state on an
/// individual artifact can be flipped independently for narrow subpoena
/// scope, but the case-level cascade is the typical path; per-artifact
/// admin is not in scope for this sprint.
/// </para>
///
/// <para>
/// <b>Audit-trailed.</b> Every reclassify / hold / release emits an
/// audit event via <see cref="IEventPublisher"/>. Best-effort emission
/// per the Sprint 28 RulesAdminService pattern — write failures log a
/// warning but do not fail the surrounding write (the entity write is
/// the source of truth; audit is reconstructable from row state).
/// </para>
///
/// <para>
/// <b>Read posture.</b> The DbContext is the per-request scoped
/// <see cref="InspectionDbContext"/>; reads are <c>AsNoTracking</c>
/// where the result isn't being mutated; writes attach the case +
/// related artifacts and call <see cref="DbContext.SaveChangesAsync"/>.
/// </para>
/// </summary>
public sealed class RetentionService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ITenantSettingsService _settings;
    private readonly IEventPublisher _events;
    private readonly ILogger<RetentionService> _logger;
    private readonly TimeProvider _clock;

    public RetentionService(
        InspectionDbContext db,
        ITenantContext tenant,
        ITenantSettingsService settings,
        IEventPublisher events,
        ILogger<RetentionService> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Admin reclassifies a case to a new retention class. Updates the
    /// case's <see cref="InspectionCase.RetentionClass"/>,
    /// <see cref="InspectionCase.RetentionClassSetAt"/>,
    /// <see cref="InspectionCase.RetentionClassSetByUserId"/>; emits
    /// <c>nickerp.inspection.retention_class_changed</c> with the old
    /// + new class + actor.
    ///
    /// <para>
    /// <b>Does not cascade to artifacts.</b> Per Sprint 39 design,
    /// scan-artifact retention is independently managed (artifacts can
    /// outlive their case for cross-case correlation). Admin can flip
    /// artifact-level retention via a future per-artifact endpoint
    /// (out of scope for this sprint).
    /// </para>
    /// </summary>
    public async Task SetRetentionClassAsync(
        Guid caseId,
        RetentionClass newClass,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var theCase = await _db.Set<InspectionCase>()
            .FirstOrDefaultAsync(c => c.Id == caseId, ct)
            ?? throw new InvalidOperationException(
                $"Case {caseId} not found (or not visible under the current tenant context).");

        var oldClass = theCase.RetentionClass;
        var now = _clock.GetUtcNow();
        theCase.RetentionClass = newClass;
        theCase.RetentionClassSetAt = now;
        theCase.RetentionClassSetByUserId = actorUserId;

        await _db.SaveChangesAsync(ct);

        await TryEmitAsync(
            eventType: "nickerp.inspection.retention_class_changed",
            entityId: caseId.ToString(),
            actorUserId: actorUserId,
            now: now,
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["caseId"] = caseId,
                ["oldClass"] = oldClass.ToString(),
                ["newClass"] = newClass.ToString(),
                ["userId"] = actorUserId
            },
            ct: ct);
    }

    /// <summary>
    /// Apply a legal hold to a case. Flips
    /// <see cref="InspectionCase.LegalHold"/> = true on the case AND on
    /// every <see cref="ScanArtifact"/> under the case in one
    /// transaction. Emits
    /// <c>nickerp.inspection.legal_hold_applied</c> with the cascade
    /// scope.
    ///
    /// <para>
    /// <b>Idempotent.</b> Re-applying a hold to a case that's already
    /// held overwrites <see cref="InspectionCase.LegalHoldAppliedAt"/>
    /// + <see cref="InspectionCase.LegalHoldAppliedByUserId"/>
    /// + <see cref="InspectionCase.LegalHoldReason"/> with the new
    /// values. Useful when an extended hold needs a refreshed reason
    /// or a different operator name on record.
    /// </para>
    /// </summary>
    /// <param name="caseId">Case to hold.</param>
    /// <param name="reason">Free-text reason; bounded to 500 chars (truncated if longer).</param>
    /// <param name="actorUserId">Operator id; null for system-applied holds (rare).</param>
    public async Task ApplyLegalHoldAsync(
        Guid caseId,
        string reason,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var bounded = reason.Length > 500 ? reason[..500] : reason;

        var theCase = await _db.Set<InspectionCase>()
            .FirstOrDefaultAsync(c => c.Id == caseId, ct)
            ?? throw new InvalidOperationException(
                $"Case {caseId} not found (or not visible under the current tenant context).");

        var now = _clock.GetUtcNow();
        theCase.LegalHold = true;
        theCase.LegalHoldAppliedAt = now;
        theCase.LegalHoldAppliedByUserId = actorUserId;
        theCase.LegalHoldReason = bounded;

        // Cascade to every artifact under every scan under the case.
        // Two-step query: find scans, then artifacts. EF in-memory
        // provider doesn't support DbSet<ScanArtifact>.Include of an
        // InspectionCase; the join goes via Scan.CaseId.
        var scanIds = await _db.Set<Scan>()
            .Where(s => s.CaseId == caseId)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var artifacts = await _db.Set<ScanArtifact>()
            .Where(a => scanIds.Contains(a.ScanId))
            .ToListAsync(ct);

        foreach (var a in artifacts)
        {
            a.LegalHold = true;
            a.LegalHoldAppliedAt = now;
            a.LegalHoldAppliedByUserId = actorUserId;
            a.LegalHoldReason = bounded;
        }

        await _db.SaveChangesAsync(ct);

        await TryEmitAsync(
            eventType: "nickerp.inspection.legal_hold_applied",
            entityId: caseId.ToString(),
            actorUserId: actorUserId,
            now: now,
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["caseId"] = caseId,
                ["reason"] = bounded,
                ["artifactCount"] = artifacts.Count,
                ["userId"] = actorUserId
            },
            ct: ct);
    }

    /// <summary>
    /// Release a legal hold from a case. Flips
    /// <see cref="InspectionCase.LegalHold"/> = false on the case AND
    /// on every <see cref="ScanArtifact"/> under the case in one
    /// transaction. Persists <see cref="InspectionCase.LegalHoldAppliedAt"/>,
    /// <see cref="InspectionCase.LegalHoldAppliedByUserId"/>,
    /// <see cref="InspectionCase.LegalHoldReason"/> for audit-trail
    /// continuity (don't null them out — they remember "this case was
    /// once held"). Emits
    /// <c>nickerp.inspection.legal_hold_released</c>.
    /// </summary>
    /// <param name="caseId">Case to release.</param>
    /// <param name="releaseReason">Free-text reason for release; bounded to 500 chars.</param>
    /// <param name="actorUserId">Operator id; null for system-released holds.</param>
    public async Task ReleaseLegalHoldAsync(
        Guid caseId,
        string releaseReason,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseReason);
        var bounded = releaseReason.Length > 500 ? releaseReason[..500] : releaseReason;

        var theCase = await _db.Set<InspectionCase>()
            .FirstOrDefaultAsync(c => c.Id == caseId, ct)
            ?? throw new InvalidOperationException(
                $"Case {caseId} not found (or not visible under the current tenant context).");

        if (!theCase.LegalHold)
            throw new InvalidOperationException(
                $"Case {caseId} is not under a legal hold; nothing to release.");

        var now = _clock.GetUtcNow();
        theCase.LegalHold = false;
        // Apply* fields persist for audit-trail continuity.

        var scanIds = await _db.Set<Scan>()
            .Where(s => s.CaseId == caseId)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var artifacts = await _db.Set<ScanArtifact>()
            .Where(a => scanIds.Contains(a.ScanId) && a.LegalHold)
            .ToListAsync(ct);

        foreach (var a in artifacts)
        {
            a.LegalHold = false;
        }

        await _db.SaveChangesAsync(ct);

        await TryEmitAsync(
            eventType: "nickerp.inspection.legal_hold_released",
            entityId: caseId.ToString(),
            actorUserId: actorUserId,
            now: now,
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["caseId"] = caseId,
                ["releaseReason"] = bounded,
                ["artifactCount"] = artifacts.Count,
                ["userId"] = actorUserId
            },
            ct: ct);
    }

    /// <summary>
    /// List every case currently under a legal hold for the given
    /// tenant. Used by the <c>/admin/retention</c> page and operator
    /// drill-downs. Ordered by
    /// <see cref="InspectionCase.LegalHoldAppliedAt"/> descending so the
    /// most recently held cases surface first.
    /// </summary>
    public async Task<IReadOnlyList<RetentionCaseRow>> ListLegalHoldsAsync(
        long tenantId,
        CancellationToken ct = default)
    {
        return await _db.Set<InspectionCase>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.LegalHold)
            .OrderByDescending(c => c.LegalHoldAppliedAt)
            .Select(c => new RetentionCaseRow(
                c.Id,
                c.SubjectIdentifier,
                c.RetentionClass,
                c.LegalHold,
                c.LegalHoldReason,
                c.LegalHoldAppliedAt,
                c.LegalHoldAppliedByUserId,
                c.RetentionClassSetAt,
                c.RetentionClassSetByUserId,
                c.OpenedAt,
                c.ClosedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Paged listing of cases for a single retention class on a single
    /// tenant. Ordered by <see cref="InspectionCase.OpenedAt"/>
    /// descending — operators usually drill in by recency.
    /// </summary>
    public async Task<IReadOnlyList<RetentionCaseRow>> ListByRetentionClassAsync(
        long tenantId,
        RetentionClass retentionClass,
        int take = 100,
        int skip = 0,
        CancellationToken ct = default)
    {
        if (take <= 0) take = 100;
        if (skip < 0) skip = 0;
        return await _db.Set<InspectionCase>()
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.RetentionClass == retentionClass)
            .OrderByDescending(c => c.OpenedAt)
            .Skip(skip)
            .Take(take)
            .Select(c => new RetentionCaseRow(
                c.Id,
                c.SubjectIdentifier,
                c.RetentionClass,
                c.LegalHold,
                c.LegalHoldReason,
                c.LegalHoldAppliedAt,
                c.LegalHoldAppliedByUserId,
                c.RetentionClassSetAt,
                c.RetentionClassSetByUserId,
                c.OpenedAt,
                c.ClosedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Resolve the retention policy for a class on the current tenant.
    /// Reads <see cref="ITenantSettingsService"/> for an int-typed
    /// tenant override under
    /// <see cref="RetentionPolicyDefaults.SettingKey"/>; falls back to
    /// <see cref="RetentionPolicyDefaults.FallbackDays"/> when no row
    /// exists or the row's value cannot be parsed as int.
    ///
    /// <para>
    /// <see cref="RetentionClass.Training"/> +
    /// <see cref="RetentionClass.LegalHold"/> always return
    /// <c>RetentionDays = int.MaxValue</c> + <c>IsAutoPurgeEligible = false</c>;
    /// these classes never auto-purge regardless of any tenant override
    /// (operator-driven release only).
    /// </para>
    /// </summary>
    public async Task<RetentionPolicy> GetRetentionPolicyAsync(
        RetentionClass retentionClass,
        CancellationToken ct = default)
    {
        var fallback = RetentionPolicyDefaults.FallbackDays(retentionClass);
        var eligible = RetentionPolicyDefaults.IsAutoPurgeEligible(retentionClass);
        var key = RetentionPolicyDefaults.SettingKey(retentionClass);

        // Training + LegalHold have no setting key — never overridable
        // from tenant_settings. Return the fallback (= int.MaxValue)
        // straight away.
        if (key is null || !_tenant.IsResolved)
        {
            return new RetentionPolicy(retentionClass, fallback, eligible, "fallback");
        }

        var resolved = await _settings.GetIntAsync(key, _tenant.TenantId, fallback, ct);
        var source = resolved == fallback ? "fallback" : "tenant-setting";
        return new RetentionPolicy(retentionClass, resolved, eligible, source);
    }

    /// <summary>
    /// Best-effort audit-event publish — logs a warning if emission
    /// fails but does not propagate. Mirrors Sprint 28 RulesAdminService.
    /// </summary>
    private async Task TryEmitAsync(
        string eventType,
        string entityId,
        Guid? actorUserId,
        DateTimeOffset now,
        IDictionary<string, object?> payload,
        CancellationToken ct)
    {
        if (!_tenant.IsResolved) return;
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            var key = IdempotencyKey.ForEntityChange(
                _tenant.TenantId, eventType, "InspectionCase", entityId, now);
            var evt = DomainEvent.Create(
                tenantId: _tenant.TenantId,
                actorUserId: actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: eventType,
                entityType: "InspectionCase",
                entityId: entityId,
                payload: json,
                idempotencyKey: key);
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "RetentionService failed to emit {EventType} for entity={EntityId} tenant={TenantId}.",
                eventType, entityId, _tenant.TenantId);
        }
    }
}

/// <summary>
/// Sprint 44 / Phase A — one row per case in the
/// <c>/admin/retention</c> + purge-candidates pages. Carries the
/// retention posture + the legal-hold posture so the page can render
/// both columns without a second query.
/// </summary>
public sealed record RetentionCaseRow(
    Guid CaseId,
    string SubjectIdentifier,
    RetentionClass RetentionClass,
    bool LegalHold,
    string? LegalHoldReason,
    DateTimeOffset? LegalHoldAppliedAt,
    Guid? LegalHoldAppliedByUserId,
    DateTimeOffset? RetentionClassSetAt,
    Guid? RetentionClassSetByUserId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt);
