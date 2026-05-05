using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Core.Validation;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Validation;

/// <summary>
/// Sprint 28 — vendor-neutral validation-rule engine.
///
/// <para>
/// One <c>ValidationEngine</c> per scope; resolves every registered
/// <see cref="IValidationRule"/> at construction (DI fan-in) and runs
/// them against an <see cref="InspectionCase"/> on demand. Outcomes
/// persist as <see cref="Finding"/> rows under a freshly-created
/// <see cref="AnalystReview"/> (owned by an "engine" review-session)
/// so the existing analyst UI can surface them without new wiring.
/// Audit events emit per outcome under
/// <c>inspection.validation.failed</c> / <c>passed</c> / <c>skipped</c>.
/// </para>
///
/// <para>
/// <b>Determinism.</b> Rules execute in <see cref="IValidationRule.RuleId"/>
/// order so two evaluations of the same case produce the same audit
/// transcript. Per-tenant disable flags are read once per evaluation
/// (one DB query) so an admin flipping a flag mid-run doesn't tear the
/// transcript.
/// </para>
///
/// <para>
/// <b>Vendor-neutral.</b> No Ghana strings live here — port codes,
/// regime codes, Fyco values, etc. all live in the CustomsGh adapter
/// (configuration-driven within the plugin). The engine treats every
/// rule as opaque: it gets a <see cref="ValidationContext"/>, gets a
/// <see cref="ValidationOutcome"/> back.
/// </para>
/// </summary>
public sealed class ValidationEngine
{
    private readonly InspectionDbContext _db;
    private readonly IReadOnlyList<IValidationRule> _rules;
    private readonly IRuleEnablementProvider _enablement;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly ILogger<ValidationEngine> _logger;

    public ValidationEngine(
        InspectionDbContext db,
        IEnumerable<IValidationRule> rules,
        IRuleEnablementProvider enablement,
        IEventPublisher events,
        ITenantContext tenant,
        ILogger<ValidationEngine> logger)
    {
        // Validate rule shape before stashing dependencies so a duplicate
        // ruleId in a misconfigured DI is the diagnostic the user sees,
        // not a downstream NullReferenceException.
        var ruleList = rules?.ToList() ?? new List<IValidationRule>();
        var dupes = ruleList
            .GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate IValidationRule.RuleId registrations: {string.Join(", ", dupes)}. "
                + "Rule ids must be unique within a deployment.");
        }

        _db = db ?? throw new ArgumentNullException(nameof(db));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Stable eval order — sort by RuleId so two evaluations of the
        // same case produce the same audit transcript.
        _rules = ruleList.OrderBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// All registered rules, sorted by <see cref="IValidationRule.RuleId"/>.
    /// Exposed for the admin UI's "list rules + recent failure counts"
    /// page. The list is live (singletons), not a snapshot — referenced
    /// rules can be queried directly.
    /// </summary>
    public IReadOnlyList<IValidationRule> Rules => _rules;

    /// <summary>
    /// Evaluate every registered rule against the case and persist
    /// outcomes as Findings + audit events. Returns the aggregated
    /// result so callers can branch on <see cref="ValidationEngineResult.HasErrors"/>.
    /// </summary>
    /// <remarks>
    /// The engine snapshot loads every Scan, Document, ScannerDeviceInstance,
    /// and ScanArtifact for the case in one EF round-trip, so individual
    /// rules can read freely from the <see cref="ValidationContext"/>
    /// without repeating queries.
    ///
    /// <para>
    /// Findings are attached to a synthetic AnalystReview (the engine
    /// row) — separate from the analyst's own review. The synthetic
    /// review's session is marked <c>Outcome="engine-validation"</c>;
    /// pages that filter analyst reviews by outcome can hide engine
    /// reviews from the workload count.
    /// </para>
    /// </remarks>
    public async Task<ValidationEngineResult> EvaluateAsync(
        Guid caseId,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ValidationEngine cannot run without a resolved tenant context.");
        var tenantId = _tenant.TenantId;

        var @case = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");

        var context = await BuildContextAsync(@case, ct);
        var disabledRuleIds = await _enablement.DisabledRuleIdsAsync(tenantId, ct);

        var outcomes = new List<ValidationOutcome>(_rules.Count);
        foreach (var rule in _rules)
        {
            if (disabledRuleIds.Contains(rule.RuleId))
            {
                _logger.LogDebug(
                    "Rule {RuleId} is disabled for tenant {TenantId}; skipping.",
                    rule.RuleId, tenantId);
                continue;
            }

            ValidationOutcome outcome;
            try
            {
                outcome = rule.Evaluate(context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Rules contract says "MUST NOT throw" but be defensive —
                // a misbehaving rule shouldn't break the rest of the
                // evaluation. Convert to a Skip + log.
                _logger.LogWarning(ex,
                    "Rule {RuleId} threw during evaluation of case {CaseId}; treating as Skip.",
                    rule.RuleId, caseId);
                outcome = ValidationOutcome.Skip(rule.RuleId, $"rule threw: {ex.GetType().Name}");
            }

            // Cross-check: outcome RuleId must match the rule that emitted
            // it. If a rule mis-tags its outcome the engine prefers the
            // rule's declared id (analytics depend on this invariant).
            if (!string.Equals(outcome.RuleId, rule.RuleId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Rule {RuleId} emitted outcome with mismatched id '{EmittedId}'; rebadging.",
                    rule.RuleId, outcome.RuleId);
                outcome = outcome with { RuleId = rule.RuleId };
            }
            outcomes.Add(outcome);
        }

        await PersistAndAuditAsync(@case, outcomes, ct);

        return new ValidationEngineResult(caseId, outcomes);
    }

    private async Task<ValidationContext> BuildContextAsync(InspectionCase @case, CancellationToken ct)
    {
        var scans = await _db.Scans.AsNoTracking()
            .Where(s => s.CaseId == @case.Id)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);
        var docs = await _db.AuthorityDocuments.AsNoTracking()
            .Where(d => d.CaseId == @case.Id)
            .OrderBy(d => d.ReceivedAt)
            .ToListAsync(ct);
        var deviceIds = scans.Select(s => s.ScannerDeviceInstanceId).Distinct().ToList();
        var devices = deviceIds.Count == 0
            ? new List<ScannerDeviceInstance>()
            : await _db.ScannerDeviceInstances.AsNoTracking()
                .Where(d => deviceIds.Contains(d.Id))
                .ToListAsync(ct);
        var scanIds = scans.Select(s => s.Id).ToList();
        var artifacts = scanIds.Count == 0
            ? new List<ScanArtifact>()
            : await _db.ScanArtifacts.AsNoTracking()
                .Where(a => scanIds.Contains(a.ScanId))
                .ToListAsync(ct);
        var location = await _db.Locations.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == @case.LocationId, ct);

        return new ValidationContext(
            Case: @case,
            Scans: scans,
            Documents: docs,
            ScannerDevices: devices,
            ScanArtifacts: artifacts,
            LocationCode: location?.Code ?? string.Empty,
            TenantId: @case.TenantId);
    }

    private async Task PersistAndAuditAsync(
        InspectionCase @case,
        IReadOnlyList<ValidationOutcome> outcomes,
        CancellationToken ct)
    {
        if (outcomes.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var tenantId = @case.TenantId;

        // Materialise findings. One synthetic AnalystReview per evaluation
        // run owns the findings — analyst reviews stay distinct (the
        // session.Outcome="engine-validation" lets pages filter).
        var findingsToWrite = outcomes
            .Where(o => o.Severity != ValidationSeverity.Skip)
            .ToList();

        ReviewSession? syntheticSession = null;
        AnalystReview? syntheticReview = null;
        if (findingsToWrite.Count > 0)
        {
            syntheticSession = new ReviewSession
            {
                CaseId = @case.Id,
                AnalystUserId = Guid.Empty,
                StartedAt = now,
                EndedAt = now,
                Outcome = "engine-validation",
                TenantId = tenantId
            };
            _db.ReviewSessions.Add(syntheticSession);

            syntheticReview = new AnalystReview
            {
                ReviewSessionId = syntheticSession.Id,
                TimeToDecisionMs = 0,
                ConfidenceScore = 1.0,
                CreatedAt = now,
                // Sprint 42 / FU-engine-emitted-reviewtype-tagging — engine-
                // emitted reviews ship with ReviewType.EngineValidation so the
                // /admin/reviews/throughput dashboard can split engine output
                // from human-emitted reviews. Sprint 34 left this defaulting
                // to ReviewType.Standard; the followup wires the explicit
                // type through.
                ReviewType = ReviewType.EngineValidation,
                TenantId = tenantId
            };
            _db.AnalystReviews.Add(syntheticReview);

            foreach (var outcome in findingsToWrite)
            {
                var locationJson = JsonSerializer.Serialize(new
                {
                    properties = outcome.Properties ?? new Dictionary<string, string>()
                });
                _db.Findings.Add(new Finding
                {
                    AnalystReviewId = syntheticReview.Id,
                    FindingType = $"validation.{outcome.RuleId}",
                    Severity = outcome.Severity.ToString().ToLowerInvariant(),
                    LocationInImageJson = locationJson,
                    Note = outcome.Message,
                    CreatedAt = now,
                    TenantId = tenantId
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // Audit events — one per outcome, including Skips so the trail is
        // complete. EventType picks the right verb per severity.
        foreach (var outcome in outcomes)
        {
            var eventType = outcome.Severity switch
            {
                ValidationSeverity.Error => "inspection.validation.failed",
                ValidationSeverity.Warning => "inspection.validation.failed",
                ValidationSeverity.Info => "inspection.validation.passed",
                ValidationSeverity.Skip => "inspection.validation.skipped",
                _ => "inspection.validation.passed"
            };

            var props = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ruleId"] = outcome.RuleId,
                ["severity"] = outcome.Severity.ToString(),
                ["message"] = outcome.Message
            };
            if (outcome.Properties is { Count: > 0 })
                props["properties"] = outcome.Properties;

            try
            {
                var json = JsonSerializer.SerializeToElement(props);
                var key = IdempotencyKey.ForEntityChange(
                    tenantId, eventType, "InspectionCase", @case.Id.ToString(), now);
                var evt = DomainEvent.Create(
                    tenantId,
                    actorUserId: null,
                    correlationId: @case.CorrelationId,
                    eventType: eventType,
                    entityType: "InspectionCase",
                    entityId: @case.Id.ToString(),
                    payload: json,
                    idempotencyKey: key);
                await _events.PublishAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to emit {EventType} for rule {RuleId} on case {CaseId}",
                    eventType, outcome.RuleId, @case.Id);
            }
        }
    }
}
