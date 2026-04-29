using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Inspection.Core.Entities;

/// <summary>
/// Per-authority rollout-phase tracking for the inbound post-hoc outcome
/// adapter (§6.11.13). One row per
/// <c>(<see cref="TenantId"/>, <see cref="ExternalSystemInstanceId"/>)</c>
/// pair — each authority instance can be in a different phase, ramped
/// independently per the rollout schedule.
///
/// <para>
/// Phases:
/// <list type="number">
/// <item>0 — Dev eval (manual entry only; no API or webhook).</item>
/// <item>1 — Shadow (pull/push wired up; outcomes write to a parallel
/// <c>pending_outcomes</c> table; not yet linked to <see cref="AnalystReview"/>).</item>
/// <item>2 — Primary + 5 % audit (link enabled;
/// <c>PostHocOutcomeUpdater</c> writes to <see cref="AnalystReview"/>; 5 %
/// of traffic also written to <c>pending_outcomes</c> for audit).</item>
/// <item>3 — Primary (audit slice retired or trimmed to 1 % drift
/// sentinel; gap-detector live; supersession path exercised).</item>
/// </list>
/// </para>
///
/// <para>
/// Each phase has a gate (≥ 14 days shadow, ≥ 30 days primary+audit, etc.)
/// that must be satisfied before promotion. <see cref="GateNotesJson"/>
/// captures evidence (mismatch-rate samples, SLO measurements, gold
/// disagreement-Spearman) so demotions are auditable.
/// </para>
/// </summary>
public sealed class PostHocRolloutPhase : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public long TenantId { get; set; }

    /// <summary>The authority instance this phase row tracks. FK → <see cref="ExternalSystemInstance"/>.</summary>
    public Guid ExternalSystemInstanceId { get; set; }
    public ExternalSystemInstance? ExternalSystemInstance { get; set; }

    /// <summary>Current rollout phase (0–3); see §6.11.13 table on the class summary.</summary>
    public PostHocRolloutPhaseValue CurrentPhase { get; set; } = PostHocRolloutPhaseValue.DevEvalManualOnly;

    /// <summary>When the current phase was entered.</summary>
    public DateTimeOffset PhaseEnteredAt { get; set; }

    /// <summary>The admin who promoted this instance to <see cref="CurrentPhase"/>. Null for the seed row at phase 0.</summary>
    public Guid? PromotedByUserId { get; set; }

    /// <summary>
    /// Gate evidence accumulator: mismatch-rate samples for shadow phase,
    /// gap-detect false-positive rate for phase 3, etc. Captured on
    /// promotion so demotions are auditable.
    /// </summary>
    public string GateNotesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The four rollout phases — see §6.11.13.</summary>
public enum PostHocRolloutPhaseValue
{
    /// <summary>Phase 0 — manual-entry tool only, no API or webhook.</summary>
    DevEvalManualOnly = 0,

    /// <summary>Phase 1 — pull/push wired but outcomes land in a parallel pending table; not yet linked.</summary>
    Shadow = 10,

    /// <summary>Phase 2 — link enabled; 5 % of traffic also written to the audit pending table.</summary>
    PrimaryPlus5PctAudit = 20,

    /// <summary>Phase 3 — audit slice retired or trimmed to a 1 % drift sentinel; gap-detector live.</summary>
    Primary = 30
}
