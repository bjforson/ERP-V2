using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.Inspection.Application.StateMachines;
using NickERP.Inspection.Application.Workflows;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Queueing.StateMachine;

namespace NickERP.Inspection.Web.Tests;

public sealed class InspectionStateMachineProjectionTests
{
    [Fact]
    public async Task ValidateTransition_UpdatesCaseProjectionAndEnqueuesSplitDetection()
    {
        var options = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("inspection-sm-projection-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        var queue = new CapturingTransactionalQueue();
        await using var db = new InspectionDbContext(options);

        var caseId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        db.Cases.Add(new InspectionCase
        {
            Id = caseId,
            TenantId = 1,
            LocationId = Guid.NewGuid(),
            SubjectIdentifier = "MSCU1234567",
            State = InspectionWorkflowState.Open,
            OpenedAt = DateTimeOffset.UtcNow,
            StateEnteredAt = DateTimeOffset.UtcNow
        });
        db.WorkItems.Add(new InspectionWorkItem
        {
            Id = workItemId,
            TenantId = 1,
            CaseId = caseId,
            IdempotencyAnchor = "inspection-case:" + caseId.ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var workItem = await db.WorkItems.SingleAsync(w => w.Id == workItemId);
        var sut = new InspectionStateMachine(queue);

        var result = await sut.TransitionAsync(
            db,
            workItem,
            InspectionTrigger.Validate,
            actor: "system/test",
            reason: "test validation",
            correlationId: "corr-test");

        result.Outcome.Should().Be(StateTransitionOutcome.Applied);

        var reloadedCase = await db.Cases.AsNoTracking().SingleAsync(c => c.Id == caseId);
        reloadedCase.State.Should().Be(InspectionWorkflowState.Validated);
        reloadedCase.StateEnteredAt.Should().BeAfter(reloadedCase.OpenedAt);

        queue.Db.Should().BeSameAs(db);
        queue.Request.Should().NotBeNull();
        queue.Request!.WorkItemId.Should().Be(workItemId);
        queue.Request.Payload.CaseId.Should().Be(caseId);
        queue.Request.Payload.ImageRef.Should().Be($"case://{caseId:N}/primary");

        var transition = await db.WorkItemTransitions.AsNoTracking().SingleAsync(t => t.WorkItemId == workItemId);
        transition.FromState.Should().Be(nameof(InspectionWorkflowState.Open));
        transition.ToState.Should().Be(nameof(InspectionWorkflowState.Validated));
    }

    private sealed class CapturingTransactionalQueue : ITransactionalQueue<SplitDetectionPayload>
    {
        public DbContext? Db { get; private set; }
        public EnqueueRequest<SplitDetectionPayload>? Request { get; private set; }

        public Task<long> EnqueueAsync(
            DbContext db,
            EnqueueRequest<SplitDetectionPayload> request,
            CancellationToken ct = default)
        {
            Db = db;
            Request = request;
            return Task.FromResult(1L);
        }
    }
}
