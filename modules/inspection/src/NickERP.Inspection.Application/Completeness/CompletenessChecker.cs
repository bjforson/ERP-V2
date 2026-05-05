using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Completeness;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Completeness;

/// <summary>
/// Sprint 31 / B5.1 — vendor-neutral completeness-rollup engine.
///
/// <para>
/// One <c>CompletenessChecker</c> per scope; resolves every registered
/// <see cref="ICompletenessRequirement"/> at construction (DI fan-in)
/// and runs them against an <see cref="InspectionCase"/> on demand.
/// Outcomes persist as <see cref="Finding"/> rows under a synthetic
/// <see cref="AnalystReview"/> tagged <c>completeness-engine</c>
/// (separate from the validation-engine review and the analyst's own
/// review). Audit events emit per outcome under
/// <c>inspection.completeness.incomplete</c> /
/// <c>...partially_complete</c> / <c>...passed</c> / <c>...skipped</c>.
/// </para>
///
/// <para>
/// <b>Determinism.</b> Requirements execute in
/// <see cref="ICompletenessRequirement.RequirementId"/> order so two
/// evaluations of the same case produce the same audit transcript.
/// Per-tenant disable + threshold flags are read once per evaluation
/// (one DB query) so an admin flipping a flag mid-run doesn't tear the
/// transcript.
/// </para>
///
/// <para>
/// <b>Vendor-neutral.</b> No Ghana strings live here — port codes,
/// regime codes, Fyco values, etc. all live in CustomsGh adapter
/// requirements. The engine treats every requirement as opaque: it
/// gets a <see cref="CompletenessContext"/>, gets a
/// <see cref="CompletenessOutcome"/> back.
/// </para>
///
/// <para>
/// <b>Coexistence with Sprint 28 ValidationEngine.</b> Validation
/// rules check <i>invariants</i> (e.g. "the Fyco field matches the
/// regime direction"); completeness requirements check
/// <i>completeness</i> (e.g. "the case has a scan, a document, and an
/// analyst decision"). They run independently — both fire on
/// validated→reviewed transitions, both persist Findings, neither
/// replaces the other. Completeness Findings carry the
/// <c>FindingType=completeness.{requirementId}</c> prefix so the
/// dashboard can filter them out from validation findings.
/// </para>
/// </summary>
public sealed class CompletenessChecker : ICompletenessChecker
{
    private readonly InspectionDbContext _db;
    private readonly IReadOnlyList<ICompletenessRequirement> _requirements;
    private readonly ICompletenessRequirementProvider _settings;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly ILogger<CompletenessChecker> _logger;

    public CompletenessChecker(
        InspectionDbContext db,
        IEnumerable<ICompletenessRequirement> requirements,
        ICompletenessRequirementProvider settings,
        IEventPublisher events,
        ITenantContext tenant,
        ILogger<CompletenessChecker> logger)
    {
        // Validate requirement shape before stashing dependencies — duplicate
        // requirementId in a misconfigured DI is the diagnostic the user
        // sees, not a downstream NullReferenceException.
        var list = requirements?.ToList() ?? new List<ICompletenessRequirement>();
        var dupes = list
            .GroupBy(r => r.RequirementId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate ICompletenessRequirement.RequirementId registrations: {string.Join(", ", dupes)}. "
                + "Requirement ids must be unique within a deployment.");
        }

        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _requirements = list
            .OrderBy(r => r.RequirementId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// All registered requirements, sorted by
    /// <see cref="ICompletenessRequirement.RequirementId"/>. Exposed for
    /// the admin UI's "list requirements + recent miss counts" page.
    /// </summary>
    public IReadOnlyList<ICompletenessRequirement> Requirements => _requirements;

    public async Task<CompletenessEvaluationResult> EvaluateAsync(
        Guid caseId,
        CancellationToken ct = default)
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "CompletenessChecker cannot run without a resolved tenant context.");
        var tenantId = _tenant.TenantId;

        var @case = await _db.Cases.FirstOrDefaultAsync(c => c.Id == caseId, ct)
            ?? throw new InvalidOperationException($"Case {caseId} not found.");

        var settings = await _settings.GetSettingsAsync(tenantId, ct);

        // Sprint 36 / FU-completeness-percent-requirements — resolve the
        // effective numeric threshold per requirement before building
        // the context: tenant override (settings.MinThreshold) wins;
        // otherwise fall back to the requirement's DefaultMinThreshold.
        // Requirements with no DefaultMinThreshold + no override don't
        // appear in the dictionary (boolean-style requirements).
        var thresholdSources = new Dictionary<string, ThresholdSource>(StringComparer.OrdinalIgnoreCase);
        var thresholds = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in _requirements)
        {
            decimal? effective = null;
            ThresholdSource source = ThresholdSource.None;
            if (settings.TryGetValue(req.RequirementId, out var snap) && snap.MinThreshold.HasValue)
            {
                effective = snap.MinThreshold.Value;
                source = ThresholdSource.TenantOverride;
            }
            else if (req.DefaultMinThreshold.HasValue)
            {
                effective = req.DefaultMinThreshold.Value;
                source = ThresholdSource.BuiltInDefault;
            }
            if (effective.HasValue)
            {
                thresholds[req.RequirementId] = effective.Value;
                thresholdSources[req.RequirementId] = source;
            }
        }

        var context = await BuildContextAsync(@case, ct, thresholds);

        var outcomes = new List<CompletenessOutcome>(_requirements.Count);
        // Sprint 36 / FU-completeness-percent-requirements — track which
        // requirements actually consulted their threshold so the engine
        // emits inspection.completeness.threshold_used only for those
        // (saves audit-row noise for requirements that abstained).
        var thresholdsUsed = new List<(string RequirementId, decimal Threshold, ThresholdSource Source, decimal? Observed)>();
        foreach (var req in _requirements)
        {
            if (settings.TryGetValue(req.RequirementId, out var snap) && !snap.Enabled)
            {
                _logger.LogDebug(
                    "Completeness requirement {RequirementId} disabled for tenant {TenantId}; skipping.",
                    req.RequirementId, tenantId);
                continue;
            }

            CompletenessOutcome outcome;
            try
            {
                outcome = req.Evaluate(context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Requirements contract says "MUST NOT throw" but be
                // defensive — a misbehaving requirement shouldn't break
                // the rest of the evaluation. Convert to a Skip + log.
                _logger.LogWarning(ex,
                    "Completeness requirement {RequirementId} threw during evaluation of case {CaseId}; treating as Skip.",
                    req.RequirementId, caseId);
                outcome = CompletenessOutcome.Skip(req.RequirementId, $"requirement threw: {ex.GetType().Name}");
            }

            // Cross-check: outcome RequirementId must match the requirement
            // that emitted it. If a requirement mis-tags its outcome the
            // engine prefers the requirement's declared id (analytics
            // depend on this invariant).
            if (!string.Equals(outcome.RequirementId, req.RequirementId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Requirement {RequirementId} emitted outcome with mismatched id '{EmittedId}'; rebadging.",
                    req.RequirementId, outcome.RequirementId);
                outcome = outcome with { RequirementId = req.RequirementId };
            }
            outcomes.Add(outcome);

            // Sprint 36 / FU-completeness-percent-requirements — if the
            // requirement consulted its threshold, record it for the
            // post-pass audit emission. Requirements signal "I used the
            // threshold" by writing the observed value into
            // outcome.Properties under "observedValue".
            if (thresholds.TryGetValue(req.RequirementId, out var thr) &&
                outcome.Properties is { } props &&
                props.TryGetValue("observedValue", out var observedRaw) &&
                decimal.TryParse(observedRaw, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var observed))
            {
                var src = thresholdSources.GetValueOrDefault(req.RequirementId, ThresholdSource.BuiltInDefault);
                thresholdsUsed.Add((req.RequirementId, thr, src, observed));
            }
        }

        await PersistAndAuditAsync(@case, outcomes, ct);
        await EmitThresholdAuditAsync(@case, thresholdsUsed, ct);

        return new CompletenessEvaluationResult(caseId, outcomes);
    }

    /// <summary>
    /// Sprint 36 / FU-completeness-percent-requirements — resolve effective
    /// numeric threshold per requirement (tenant-override → built-in
    /// default). Exposed so callers wanting to inspect resolved
    /// thresholds without running an evaluation (e.g. the admin UI) can
    /// reuse the same precedence rules.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, decimal>> ResolveEffectiveThresholdsAsync(
        long tenantId, CancellationToken ct = default)
    {
        var settings = await _settings.GetSettingsAsync(tenantId, ct);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in _requirements)
        {
            if (settings.TryGetValue(req.RequirementId, out var snap) && snap.MinThreshold.HasValue)
            {
                result[req.RequirementId] = snap.MinThreshold.Value;
            }
            else if (req.DefaultMinThreshold.HasValue)
            {
                result[req.RequirementId] = req.DefaultMinThreshold.Value;
            }
        }
        return result;
    }

    private async Task<CompletenessContext> BuildContextAsync(
        InspectionCase @case,
        CancellationToken ct,
        IReadOnlyDictionary<string, decimal>? thresholds = null)
    {
        var scans = await _db.Scans.AsNoTracking()
            .Where(s => s.CaseId == @case.Id)
            .OrderBy(s => s.CapturedAt)
            .ToListAsync(ct);
        var docs = await _db.AuthorityDocuments.AsNoTracking()
            .Where(d => d.CaseId == @case.Id)
            .OrderBy(d => d.ReceivedAt)
            .ToListAsync(ct);
        var scanIds = scans.Select(s => s.Id).ToList();
        var artifacts = scanIds.Count == 0
            ? new List<ScanArtifact>()
            : await _db.ScanArtifacts.AsNoTracking()
                .Where(a => scanIds.Contains(a.ScanId))
                .ToListAsync(ct);
        var sessions = await _db.ReviewSessions.AsNoTracking()
            .Where(s => s.CaseId == @case.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);
        var reviews = sessions.Count == 0
            ? new List<AnalystReview>()
            : await _db.AnalystReviews.AsNoTracking()
                .Where(r => sessions.Contains(r.ReviewSessionId))
                .ToListAsync(ct);
        var verdicts = await _db.Verdicts.AsNoTracking()
            .Where(v => v.CaseId == @case.Id)
            .ToListAsync(ct);

        return new CompletenessContext(
            Case: @case,
            Scans: scans,
            ScanArtifacts: artifacts,
            Documents: docs,
            AnalystReviews: reviews,
            Verdicts: verdicts,
            TenantId: @case.TenantId,
            Thresholds: thresholds);
    }

    /// <summary>
    /// Sprint 36 / FU-completeness-percent-requirements — emit one
    /// <c>inspection.completeness.threshold_used</c> audit per
    /// requirement that consulted its effective threshold during the
    /// run. The dashboard uses these to surface "what threshold did
    /// the engine use for this run?" without re-resolving the precedence.
    /// </summary>
    private async Task EmitThresholdAuditAsync(
        InspectionCase @case,
        IReadOnlyList<(string RequirementId, decimal Threshold, ThresholdSource Source, decimal? Observed)> consulted,
        CancellationToken ct)
    {
        if (consulted.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in consulted)
        {
            var props = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["requirementId"] = entry.RequirementId,
                ["threshold"] = entry.Threshold,
                ["source"] = entry.Source switch
                {
                    ThresholdSource.TenantOverride => "tenant-override",
                    ThresholdSource.BuiltInDefault => "built-in-default",
                    _ => "none"
                },
                ["observedValue"] = entry.Observed
            };

            try
            {
                var json = JsonSerializer.SerializeToElement(props);
                var key = IdempotencyKey.ForEntityChange(
                    @case.TenantId, "inspection.completeness.threshold_used",
                    "InspectionCase", @case.Id.ToString(), now);
                var evt = DomainEvent.Create(
                    @case.TenantId,
                    actorUserId: null,
                    correlationId: @case.CorrelationId,
                    eventType: "inspection.completeness.threshold_used",
                    entityType: "InspectionCase",
                    entityId: @case.Id.ToString(),
                    payload: json,
                    idempotencyKey: key);
                await _events.PublishAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to emit inspection.completeness.threshold_used for requirement {RequirementId} on case {CaseId}",
                    entry.RequirementId, @case.Id);
            }
        }
    }

    /// <summary>
    /// Sprint 36 / FU-completeness-percent-requirements — origin of the
    /// threshold the engine handed a percent-based requirement.
    /// </summary>
    private enum ThresholdSource
    {
        None = 0,
        TenantOverride = 1,
        BuiltInDefault = 2
    }

    private async Task PersistAndAuditAsync(
        InspectionCase @case,
        IReadOnlyList<CompletenessOutcome> outcomes,
        CancellationToken ct)
    {
        if (outcomes.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        var tenantId = @case.TenantId;

        // Materialise findings. One synthetic AnalystReview per evaluation
        // run owns the findings — analyst reviews stay distinct (the
        // session.Outcome="completeness-engine" lets pages filter).
        var findingsToWrite = outcomes
            .Where(o => o.Severity != CompletenessSeverity.Skip
                     && o.Severity != CompletenessSeverity.Pass)
            .ToList();

        if (findingsToWrite.Count > 0)
        {
            var syntheticSession = new ReviewSession
            {
                CaseId = @case.Id,
                AnalystUserId = Guid.Empty,
                StartedAt = now,
                EndedAt = now,
                Outcome = "completeness-engine",
                TenantId = tenantId
            };
            _db.ReviewSessions.Add(syntheticSession);

            var syntheticReview = new AnalystReview
            {
                ReviewSessionId = syntheticSession.Id,
                TimeToDecisionMs = 0,
                ConfidenceScore = 1.0,
                CreatedAt = now,
                // Sprint 42 / FU-engine-emitted-reviewtype-tagging — engine-
                // emitted reviews ship with ReviewType.EngineCompleteness so
                // the /admin/reviews/throughput dashboard can split
                // completeness-engine output from human-emitted reviews.
                // Sprint 34 left this defaulting to ReviewType.Standard;
                // the followup wires the explicit type through.
                ReviewType = ReviewType.EngineCompleteness,
                TenantId = tenantId
            };
            _db.AnalystReviews.Add(syntheticReview);

            foreach (var outcome in findingsToWrite)
            {
                var locationJson = JsonSerializer.Serialize(new
                {
                    properties = outcome.Properties ?? new Dictionary<string, string>(),
                    missingFields = outcome.MissingFields ?? Array.Empty<string>()
                });
                _db.Findings.Add(new Finding
                {
                    AnalystReviewId = syntheticReview.Id,
                    FindingType = $"completeness.{outcome.RequirementId}",
                    Severity = outcome.Severity.ToString().ToLowerInvariant(),
                    LocationInImageJson = locationJson,
                    Note = outcome.Message,
                    CreatedAt = now,
                    TenantId = tenantId
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        // Audit events — one per outcome including Skips so the trail is
        // complete. EventType picks the right verb per severity.
        foreach (var outcome in outcomes)
        {
            var eventType = outcome.Severity switch
            {
                CompletenessSeverity.Incomplete => "inspection.completeness.incomplete",
                CompletenessSeverity.PartiallyComplete => "inspection.completeness.partially_complete",
                CompletenessSeverity.Pass => "inspection.completeness.passed",
                CompletenessSeverity.Skip => "inspection.completeness.skipped",
                _ => "inspection.completeness.passed"
            };

            var props = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["requirementId"] = outcome.RequirementId,
                ["severity"] = outcome.Severity.ToString(),
                ["message"] = outcome.Message
            };
            if (outcome.MissingFields is { Count: > 0 })
                props["missingFields"] = outcome.MissingFields;
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
                    "Failed to emit {EventType} for requirement {RequirementId} on case {CaseId}",
                    eventType, outcome.RequirementId, @case.Id);
            }
        }
    }
}
