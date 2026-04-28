using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Events;

namespace NickERP.Platform.Tests;

/// <summary>
/// G1 #2 — audit channel routing must scope subscribers to the
/// <em>module</em> segment, not the suite-wide first segment. Pre-G1 the
/// channel was always <c>"nickerp"</c>, so a NickFinance subscriber got
/// every Inspection event and had to filter in-process.
/// </summary>
public class AuditChannelRoutingTests
{
    [Fact]
    public void ChannelFor_picks_the_two_segment_module_prefix()
    {
        // Module-scoped events resolve to nickerp.<module>.
        DbEventPublisher.ChannelFor("nickerp.finance.transaction_recorded")
            .Should().Be("nickerp.finance");
        DbEventPublisher.ChannelFor("nickerp.inspection.case_opened")
            .Should().Be("nickerp.inspection");
    }

    [Fact]
    public void ChannelFor_falls_back_for_underspecified_events()
    {
        DbEventPublisher.ChannelFor("singletoken").Should().Be("singletoken");
        DbEventPublisher.ChannelFor("two.segments").Should().Be("two.segments");
        DbEventPublisher.ChannelFor("").Should().Be("");
    }

    [Fact]
    public async Task Bus_subscriber_to_finance_channel_does_not_see_inspection_events()
    {
        var bus = new InProcessEventBus(NullLogger<InProcessEventBus>.Instance);
        var financeSeen = new List<string>();
        var inspectionSeen = new List<string>();

        await using var financeSub = bus.Subscribe("nickerp.finance",
            (e, _) => { financeSeen.Add(e.EventType); return Task.CompletedTask; });
        await using var inspectionSub = bus.Subscribe("nickerp.inspection",
            (e, _) => { inspectionSeen.Add(e.EventType); return Task.CompletedTask; });

        // Publisher's job is to invoke ChannelFor; here we exercise the
        // bus directly by emitting on the channel that the publisher
        // would have computed.
        var finEvt = MakeEvent("nickerp.finance.transaction_recorded");
        var insEvt = MakeEvent("nickerp.inspection.case_opened");
        await bus.PublishAsync(DbEventPublisher.ChannelFor(finEvt.EventType), finEvt);
        await bus.PublishAsync(DbEventPublisher.ChannelFor(insEvt.EventType), insEvt);

        financeSeen.Should().ContainSingle().Which.Should().Be("nickerp.finance.transaction_recorded");
        inspectionSeen.Should().ContainSingle().Which.Should().Be("nickerp.inspection.case_opened");
    }

    private static DomainEvent MakeEvent(string eventType) => new(
        EventId: Guid.NewGuid(),
        TenantId: 1,
        ActorUserId: null,
        CorrelationId: null,
        EventType: eventType,
        EntityType: "Test",
        EntityId: "1",
        Payload: JsonSerializer.SerializeToElement(new { x = 1 }),
        OccurredAt: DateTimeOffset.UtcNow,
        IngestedAt: DateTimeOffset.UtcNow,
        IdempotencyKey: Guid.NewGuid().ToString("N"),
        PrevEventHash: null);
}
