using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Portal.Services;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 43 Phase E — exercises the portal-side
/// <see cref="InspectionPilotProbeDataSource"/> against the EF
/// in-memory provider. Verifies the IsSynthetic flag is honoured by
/// the analyst gate query, the OutboundSubmission Status filter is
/// "accepted" + LastAttemptAt not null, and the latest-real-case-id
/// query returns the expected case.
/// </summary>
public sealed class InspectionPilotProbeDataSourceTests
{
    private const long TenantId = 5;

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasDecisionedRealCase_OnlyVerdictedNonSyntheticCase_ReturnsTrue()
    {
        await using var db = BuildDb();
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            TenantId = TenantId,
            IsSynthetic = false,
            SubjectIdentifier = "TEST123",
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
        });
        db.Verdicts.Add(new Verdict
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            TenantId = TenantId,
            Decision = VerdictDecision.Clear,
            DecidedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.HasDecisionedRealCaseAsync(TenantId);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasDecisionedRealCase_OnlySyntheticVerdictedCase_ReturnsFalse()
    {
        await using var db = BuildDb();
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            TenantId = TenantId,
            IsSynthetic = true, // ← key flag — synthetic test data
            SubjectIdentifier = "FAKE-1",
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
        });
        db.Verdicts.Add(new Verdict
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            TenantId = TenantId,
            Decision = VerdictDecision.Clear,
            DecidedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.HasDecisionedRealCaseAsync(TenantId);

        result.Should().BeFalse(
            "the only verdicted case is synthetic — the gate must not falsely pass on test data");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasDecisionedRealCase_NonSyntheticCaseWithoutVerdict_ReturnsFalse()
    {
        await using var db = BuildDb();
        db.Cases.Add(new InspectionCase
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            IsSynthetic = false,
            SubjectIdentifier = "OPEN-1",
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.HasDecisionedRealCaseAsync(TenantId);

        result.Should().BeFalse(
            "case has not been decisioned — no Verdict row exists");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasDecisionedRealCase_DifferentTenant_ReturnsFalse()
    {
        await using var db = BuildDb();
        var caseId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            TenantId = 99, // different tenant
            IsSynthetic = false,
            SubjectIdentifier = "OTHER-1",
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow,
        });
        db.Verdicts.Add(new Verdict
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            TenantId = 99,
            Decision = VerdictDecision.Clear,
            DecidedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.HasDecisionedRealCaseAsync(TenantId);

        result.Should().BeFalse(
            "the only verdicted case belongs to a different tenant");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasSuccessfulOutboundSubmission_AcceptedRowWithLastAttempt_ReturnsTrue()
    {
        await using var db = BuildDb();
        db.OutboundSubmissions.Add(new OutboundSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CaseId = Guid.NewGuid(),
            ExternalSystemInstanceId = Guid.NewGuid(),
            Status = "accepted",
            IdempotencyKey = "test-1",
            LastAttemptAt = DateTimeOffset.UtcNow,
            SubmittedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.HasSuccessfulOutboundSubmissionAsync(TenantId);

        result.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasSuccessfulOutboundSubmission_PendingStatus_ReturnsFalse()
    {
        await using var db = BuildDb();
        db.OutboundSubmissions.Add(new OutboundSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CaseId = Guid.NewGuid(),
            ExternalSystemInstanceId = Guid.NewGuid(),
            Status = "pending",
            IdempotencyKey = "test-1",
            LastAttemptAt = DateTimeOffset.UtcNow,
            SubmittedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.HasSuccessfulOutboundSubmissionAsync(TenantId);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task HasSuccessfulOutboundSubmission_AcceptedButNoLastAttempt_ReturnsFalse()
    {
        // Defensive: a seeder might preset Status without
        // LastAttemptAt; the gate refuses these as "not really
        // dispatched yet".
        await using var db = BuildDb();
        db.OutboundSubmissions.Add(new OutboundSubmission
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            CaseId = Guid.NewGuid(),
            ExternalSystemInstanceId = Guid.NewGuid(),
            Status = "accepted",
            IdempotencyKey = "test-1",
            LastAttemptAt = null,
            SubmittedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.HasSuccessfulOutboundSubmissionAsync(TenantId);

        result.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LatestDecisionedRealCaseId_ReturnsMostRecentVerdictedCase()
    {
        await using var db = BuildDb();
        var olderId = Guid.NewGuid();
        var newerId = Guid.NewGuid();
        db.Cases.AddRange(
            new InspectionCase
            {
                Id = olderId,
                TenantId = TenantId,
                IsSynthetic = false,
                SubjectIdentifier = "OLDER",
                OpenedAt = DateTimeOffset.UtcNow.AddHours(-2),
                StateEnteredAt = DateTimeOffset.UtcNow,
            },
            new InspectionCase
            {
                Id = newerId,
                TenantId = TenantId,
                IsSynthetic = false,
                SubjectIdentifier = "NEWER",
                OpenedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                StateEnteredAt = DateTimeOffset.UtcNow,
            });
        db.Verdicts.AddRange(
            new Verdict
            {
                Id = Guid.NewGuid(),
                CaseId = olderId,
                TenantId = TenantId,
                Decision = VerdictDecision.Clear,
                DecidedAt = DateTimeOffset.UtcNow,
            },
            new Verdict
            {
                Id = Guid.NewGuid(),
                CaseId = newerId,
                TenantId = TenantId,
                Decision = VerdictDecision.Clear,
                DecidedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var sut = new InspectionPilotProbeDataSource(db);
        var result = await sut.LatestDecisionedRealCaseIdAsync(TenantId);

        result.Should().Be(newerId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LatestDecisionedRealCaseId_NoCases_ReturnsNull()
    {
        await using var db = BuildDb();
        var sut = new InspectionPilotProbeDataSource(db);

        var result = await sut.LatestDecisionedRealCaseIdAsync(TenantId);

        result.Should().BeNull();
    }

    private static InspectionDbContext BuildDb()
    {
        var name = "inspection-probe-" + Guid.NewGuid();
        var opts = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new InspectionDbContext(opts);
    }
}
