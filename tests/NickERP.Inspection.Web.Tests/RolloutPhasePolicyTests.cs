using NickERP.Inspection.Application.PostHocOutcomes;
using NickERP.Inspection.Core.Entities;
using Xunit;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 13 / §6.11 — pure tests for the phase-policy mapping. Locks
/// in the §6.11.13 phase semantics so a future refactor can't silently
/// flip Shadow into emit-mode (which would leak shadow rows into the
/// §6.4 priority signal).
/// </summary>
public sealed class RolloutPhasePolicyTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(PostHocRolloutPhaseValue.DevEvalManualOnly, false)]
    [InlineData(PostHocRolloutPhaseValue.Shadow, true)]
    [InlineData(PostHocRolloutPhaseValue.PrimaryPlus5PctAudit, true)]
    [InlineData(PostHocRolloutPhaseValue.Primary, true)]
    public void ShouldPullOnCycle_MatchesPhaseSpec(PostHocRolloutPhaseValue phase, bool expected)
    {
        Assert.Equal(expected, RolloutPhasePolicy.ShouldPullOnCycle(phase));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(PostHocRolloutPhaseValue.DevEvalManualOnly, true)]   // manual entries always count
    [InlineData(PostHocRolloutPhaseValue.Shadow, false)]              // shadow persists but does NOT emit signal
    [InlineData(PostHocRolloutPhaseValue.PrimaryPlus5PctAudit, true)] // link enabled
    [InlineData(PostHocRolloutPhaseValue.Primary, true)]
    public void ShouldEmitTrainingSignal_MatchesPhaseSpec(PostHocRolloutPhaseValue phase, bool expected)
    {
        Assert.Equal(expected, RolloutPhasePolicy.ShouldEmitTrainingSignal(phase));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(PostHocRolloutPhaseValue.DevEvalManualOnly, false)]
    [InlineData(PostHocRolloutPhaseValue.Shadow, false)]
    [InlineData(PostHocRolloutPhaseValue.PrimaryPlus5PctAudit, true)]
    [InlineData(PostHocRolloutPhaseValue.Primary, true)]
    public void ShouldOverridePriorClassification_MatchesPhaseSpec(PostHocRolloutPhaseValue phase, bool expected)
    {
        Assert.Equal(expected, RolloutPhasePolicy.ShouldOverridePriorClassification(phase));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(PostHocRolloutPhaseValue.DevEvalManualOnly, "dev-eval-manual")]
    [InlineData(PostHocRolloutPhaseValue.Shadow, "shadow")]
    [InlineData(PostHocRolloutPhaseValue.PrimaryPlus5PctAudit, "primary-plus-5pct-audit")]
    [InlineData(PostHocRolloutPhaseValue.Primary, "primary")]
    public void PhaseLabel_StableAcrossPhases(PostHocRolloutPhaseValue phase, string expected)
    {
        Assert.Equal(expected, RolloutPhasePolicy.PhaseLabel(phase));
    }
}

/// <summary>
/// Locks in the §6.11.7 idempotency-key derivation. A change to the
/// formula would silently invalidate every prior idempotency dedup
/// result — so guard the exact byte-for-byte hash for a known input.
/// </summary>
public sealed class IdempotencyKeyTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeIdempotencyKey_DeterministicForFixedInput()
    {
        var record = new PostHocOutcomeRecord(
            TenantId: 1,
            ExternalSystemInstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AuthorityCode: "ICUMS-GH",
            DeclarationNumber: "C-2026-04-28-073491",
            ContainerNumber: "MSCU1234567",
            DecidedAt: new DateTimeOffset(2026, 4, 26, 13, 11, 0, TimeSpan.Zero),
            DecisionReference: "GRA-SEIZURE-2026-0241",
            SupersedesDecisionReference: null,
            PayloadJson: "{}",
            Phase: PostHocRolloutPhaseValue.Primary,
            EntryMethod: "api");

        var key1 = PostHocOutcomeWriter.ComputeIdempotencyKey(record);
        var key2 = PostHocOutcomeWriter.ComputeIdempotencyKey(record);

        Assert.Equal(key1, key2);
        Assert.Equal(64, key1.Length); // sha256 hex
        Assert.Matches("^[0-9a-f]{64}$", key1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeIdempotencyKey_ChangesOnDecisionReferenceMutation()
    {
        var baseRecord = new PostHocOutcomeRecord(
            TenantId: 1,
            ExternalSystemInstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AuthorityCode: "ICUMS-GH",
            DeclarationNumber: "C-2026-04-28-073491",
            ContainerNumber: null,
            DecidedAt: new DateTimeOffset(2026, 4, 26, 13, 11, 0, TimeSpan.Zero),
            DecisionReference: "GRA-SEIZURE-2026-0241",
            SupersedesDecisionReference: null,
            PayloadJson: "{}",
            Phase: PostHocRolloutPhaseValue.Primary,
            EntryMethod: "api");

        var keyA = PostHocOutcomeWriter.ComputeIdempotencyKey(baseRecord);
        var keyB = PostHocOutcomeWriter.ComputeIdempotencyKey(baseRecord with { DecisionReference = "GRA-SEIZURE-2026-0242" });

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComputeIdempotencyKey_ChangesOnDecidedAtMutation()
    {
        var baseRecord = new PostHocOutcomeRecord(
            TenantId: 1,
            ExternalSystemInstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            AuthorityCode: "ICUMS-GH",
            DeclarationNumber: "C-2026-04-28-073491",
            ContainerNumber: null,
            DecidedAt: new DateTimeOffset(2026, 4, 26, 13, 11, 0, TimeSpan.Zero),
            DecisionReference: "GRA-SEIZURE-2026-0241",
            SupersedesDecisionReference: null,
            PayloadJson: "{}",
            Phase: PostHocRolloutPhaseValue.Primary,
            EntryMethod: "api");

        var keyA = PostHocOutcomeWriter.ComputeIdempotencyKey(baseRecord);
        var keyB = PostHocOutcomeWriter.ComputeIdempotencyKey(baseRecord with { DecidedAt = baseRecord.DecidedAt.AddSeconds(1) });

        Assert.NotEqual(keyA, keyB);
    }
}
