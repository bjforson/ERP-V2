namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Sprint 34 / B6 — kind of review work performed on an
/// <see cref="InspectionCase"/>. Drives the dispatch of the analyst /
/// supervisor across the v1-parity review pages
/// (<c>BlReview.razor</c> / <c>AiTriage.razor</c> /
/// <c>AuditReview.razor</c>) and segregates rows on
/// <see cref="AnalystReview"/> for the throughput dashboards.
///
/// <para>
/// Vendor-neutral by design — these labels describe the analyst's
/// task shape, not any specific authority's terminology. Adapter
/// modules can layer on more specialised UI / persistence on top of
/// the same workflow plumbing without forcing new enum values into
/// the core domain.
/// </para>
///
/// <para>
/// <b>Stable values</b> (do not renumber). Persisted as <c>int</c>
/// via EF's <c>HasConversion&lt;int&gt;</c> so historical rows keep
/// their meaning across deploys; new types append.
/// </para>
/// </summary>
public enum ReviewType
{
    /// <summary>
    /// Analyst's primary image-decision review (legacy default).
    /// Existing AnalystReview rows from Sprints 1-33 land here on
    /// migration backfill — their workflow is the standard
    /// scan-image → verdict pass already covered by
    /// <c>CaseWorkflowService.SetVerdictAsync</c>.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// B6.1 Bill of Lading review — analyst verifies the BL header
    /// fields (consignee / commodity / weight / port-of-origin /
    /// port-of-destination, etc.) against the scan + manifest data
    /// before the case advances. v1's BLReview workflow grouped
    /// containers by master BL; the v2 entity-level analog is to
    /// stamp <see cref="ReviewType.BlReview"/> on the
    /// AnalystReview row whose Findings carry the per-field verified
    /// flags (<c>review.bl.field.{name}.verified</c> /
    /// <c>review.bl.field.{name}.discrepancy</c>).
    /// </summary>
    BlReview = 10,

    /// <summary>
    /// B6.2 AI triage review — analyst confirms / overrides / annotates
    /// the AI verdict + confidence + region overlays produced by the
    /// Sprint 6.5 / 6.11 inference pipeline. The analyst's response
    /// persists as Findings with FindingType prefix
    /// <c>review.ai_triage.</c> (e.g. <c>review.ai_triage.confirmed</c>,
    /// <c>review.ai_triage.overridden</c>,
    /// <c>review.ai_triage.escalated</c>); the AnalystReview row's
    /// <see cref="AnalystReview.ConfidenceScore"/> records the
    /// analyst's own confidence in the triage decision (separate
    /// from the AI's confidence, which lives on the inference output).
    /// </summary>
    AiTriage = 20,

    /// <summary>
    /// B6.3 Supervisor second-opinion / audit review. Surfaces every
    /// prior <see cref="AnalystReview"/> row + Findings + AI outputs
    /// for a case so a supervisor can record concur / dissent /
    /// escalate. Persists as a fresh AnalystReview row with this
    /// <see cref="ReviewType"/>; Findings prefix is
    /// <c>review.audit.</c> (e.g. <c>review.audit.concur</c>,
    /// <c>review.audit.dissent</c>, <c>review.audit.escalated</c>).
    /// </summary>
    AuditReview = 30,

    /// <summary>
    /// Engine-emitted validation review. Sprint 28's
    /// ValidationEngine writes Findings with
    /// FindingType=<c>validation.{ruleId}.{outcome}</c>; those
    /// Findings hang off an AnalystReview row whose ReviewType is
    /// <see cref="EngineValidation"/>. This isn't the analyst's work
    /// — it's the engine's output classified the same way so the
    /// throughput dashboards can split engine vs human runs cleanly.
    /// </summary>
    EngineValidation = 40,

    /// <summary>
    /// Engine-emitted completeness rollup review. Sprint 31's
    /// CompletenessChecker writes Findings with
    /// FindingType=<c>completeness.{requirementId}</c>; classified
    /// the same way as <see cref="EngineValidation"/> — separate
    /// bucket on the dashboard for engine vs human review counts.
    /// </summary>
    EngineCompleteness = 50,
}

/// <summary>
/// Sprint 34 / B6 — priority bucket for an
/// <see cref="InspectionCase"/>'s review queue position. Aligns with
/// v1 NSCIM's queue priorities; vendor-neutral so per-tenant SLA
/// tiers can map without leaking specific time budgets into the
/// core domain.
///
/// <para>
/// <b>Stable values.</b> Persisted as <c>int</c>; new buckets append.
/// </para>
/// </summary>
public enum ReviewQueue
{
    /// <summary>Default lane — no priority adjustment.</summary>
    Standard = 0,

    /// <summary>Operator-flagged for priority handling (e.g. analyst SLA at risk).</summary>
    HighPriority = 10,

    /// <summary>Top of the queue — supervisor or system-tagged urgent.</summary>
    Urgent = 20,

    /// <summary>Out-of-band: rule-violation triage / cross-record split / similar exception.</summary>
    Exception = 30,

    /// <summary>Post-clearance / late-arrival follow-up: review without blocking the live lane.</summary>
    PostClearance = 40,
}
