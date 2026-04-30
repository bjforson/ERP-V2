using NickERP.Inspection.Core.Entities;

namespace NickERP.Inspection.Application.PostHocOutcomes;

/// <summary>
/// Pure mapping from <see cref="PostHocRolloutPhaseValue"/> to the runtime
/// behavior gates the post-hoc outcome pull worker + downstream handler
/// observe. No I/O, no time, no DB — testable in isolation, reused by both
/// the worker (decides whether to call the adapter at all) and the
/// post-hoc handler (decides whether to update <see cref="AnalystReview.PostHocOutcomeJson"/>).
///
/// <para>Spec: IMAGE-ANALYSIS-MODERNIZATION.md §6.11.13.</para>
///
/// <list type="number">
/// <item>Phase 0 — DevEvalManualOnly. The worker entirely skips this
/// authority. Manual-entry path still works.</item>
/// <item>Phase 1 — Shadow. Pull/push wired up; outcomes <em>are</em>
/// persisted (so we can spot-check correctness against the authority
/// source-of-record), but downstream <c>PostHocOutcomeUpdater</c>
/// MUST NOT update <see cref="AnalystReview.PostHocOutcomeJson"/> — the
/// §6.4 active-learning extractor must not consume shadow-phase rows.</item>
/// <item>Phase 2 — PrimaryPlus5PctAudit. Worker pulls; handler updates
/// <c>AnalystReview.PostHocOutcomeJson</c>; §6.4 consumes.</item>
/// <item>Phase 3 — Primary. Same persistence path as phase 2, plus
/// supersession-correction overrides supersede the prior automated /
/// analyst classification on <see cref="AnalystReview.PostHocOutcomeJson"/>.
/// In v0 this is implemented by the handler appending to the
/// <c>supersedes_chain</c> in the normalised JSON; the §6.4 extractor
/// reads the latest non-superseded entry.</item>
/// </list>
/// </summary>
public static class RolloutPhasePolicy
{
    /// <summary>
    /// True when the worker should actively pull from this authority on
    /// its scheduled cycle. Phase 0 returns false — manual-entry is the
    /// only ingest path.
    /// </summary>
    public static bool ShouldPullOnCycle(PostHocRolloutPhaseValue phase) => phase switch
    {
        PostHocRolloutPhaseValue.DevEvalManualOnly => false,
        PostHocRolloutPhaseValue.Shadow => true,
        PostHocRolloutPhaseValue.PrimaryPlus5PctAudit => true,
        PostHocRolloutPhaseValue.Primary => true,
        _ => false
    };

    /// <summary>
    /// True when persisted outcomes should propagate into
    /// <see cref="AnalystReview.PostHocOutcomeJson"/> (the §6.4 priority
    /// signal). Shadow phase persists-only — pulled rows live in
    /// <c>authority_documents</c> for spot-check, but the analyst-review
    /// JSON is left untouched until phase 2.
    /// </summary>
    public static bool ShouldEmitTrainingSignal(PostHocRolloutPhaseValue phase) => phase switch
    {
        PostHocRolloutPhaseValue.DevEvalManualOnly => true, // manual entries always count
        PostHocRolloutPhaseValue.Shadow => false,
        PostHocRolloutPhaseValue.PrimaryPlus5PctAudit => true,
        PostHocRolloutPhaseValue.Primary => true,
        _ => false
    };

    /// <summary>
    /// True when supersession corrections at this phase override the
    /// prior automated classification carried in
    /// <see cref="AnalystReview.PostHocOutcomeJson"/>. Phase 0 + 1 don't
    /// override (no link enabled); phase 2 + 3 do — phase 3 is the
    /// "Primary" steady state where the gap-detector is live and
    /// supersession is exercised in production.
    /// </summary>
    public static bool ShouldOverridePriorClassification(PostHocRolloutPhaseValue phase) => phase switch
    {
        PostHocRolloutPhaseValue.DevEvalManualOnly => false,
        PostHocRolloutPhaseValue.Shadow => false,
        PostHocRolloutPhaseValue.PrimaryPlus5PctAudit => true,
        PostHocRolloutPhaseValue.Primary => true,
        _ => false
    };

    /// <summary>
    /// Source label that gets stamped into <c>payload.posthoc_phase</c>
    /// on every persisted <c>AuthorityDocument</c> so downstream
    /// consumers (analyst UI banner, §6.4 extractor) can filter out
    /// shadow rows without having to join back to
    /// <c>posthoc_rollout_phase</c>.
    /// </summary>
    public static string PhaseLabel(PostHocRolloutPhaseValue phase) => phase switch
    {
        PostHocRolloutPhaseValue.DevEvalManualOnly => "dev-eval-manual",
        PostHocRolloutPhaseValue.Shadow => "shadow",
        PostHocRolloutPhaseValue.PrimaryPlus5PctAudit => "primary-plus-5pct-audit",
        PostHocRolloutPhaseValue.Primary => "primary",
        _ => "unknown"
    };
}
