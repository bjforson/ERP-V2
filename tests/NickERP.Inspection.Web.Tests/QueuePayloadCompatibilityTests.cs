using System.Text.Json;
using NickERP.Inspection.Application.Workflows;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint S+3 / B5 — covers JSON wire-format compatibility for the two
/// inspection-side queue payloads. Both records added nullable extension
/// fields after the queue tables shipped (legacy producers had only the
/// positional fields). The tests pin the two contracts the codebase relies
/// on:
/// <list type="bullet">
///   <item><b>Backward compat.</b> Rows enqueued before the extension fields
///         existed (positional shape only) still deserialize, with the new
///         fields defaulting to null. Consumers fall back to lookup by
///         CaseId.</item>
///   <item><b>Forward compat.</b> Rows enqueued with extra fields the
///         consumer does not know about deserialize cleanly under
///         <c>JsonSerializerDefaults.Web</c>.</item>
/// </list>
/// Platform-level idempotency / dedup tests live in
/// <c>tests/NickERP.Platform.Tests/Queueing/Services/PostgresQueueIntegrationTests.cs</c>
/// — those exercise the queue mechanics against a real Postgres fixture.
/// </summary>
public sealed class QueuePayloadCompatibilityTests
{
    private static readonly JsonSerializerOptions WebOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public void OutboundSubmissionPayload_LegacyJson_DeserializesWithNullExtensionFields()
    {
        // Shape stored by pre-S+3 producers: only the positional record
        // fields, no OutboundSubmissionId / ExternalSystemInstanceId /
        // IdempotencyKey.
        var legacyJson = """
        {
          "workItemId": "11111111-1111-1111-1111-111111111111",
          "caseId":     "22222222-2222-2222-2222-222222222222",
          "enqueuedAt": "2026-05-13T10:00:00+00:00"
        }
        """;

        var payload = JsonSerializer.Deserialize<OutboundSubmissionPayload>(legacyJson, WebOptions);

        payload.Should().NotBeNull();
        payload!.WorkItemId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        payload.CaseId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        payload.OutboundSubmissionId.Should().BeNull(
            because: "legacy payloads predate this field; consumers fall back to case/idempotency lookup");
        payload.ExternalSystemInstanceId.Should().BeNull();
        payload.IdempotencyKey.Should().BeNull();
    }

    [Fact]
    public void OutboundSubmissionPayload_NewJson_RoundTrips()
    {
        var original = new OutboundSubmissionPayload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow)
        {
            OutboundSubmissionId = Guid.NewGuid(),
            ExternalSystemInstanceId = Guid.NewGuid(),
            IdempotencyKey = "verdict:caseA:icums:1"
        };

        var json = JsonSerializer.Serialize(original, WebOptions);
        var round = JsonSerializer.Deserialize<OutboundSubmissionPayload>(json, WebOptions);

        round.Should().NotBeNull();
        round!.OutboundSubmissionId.Should().Be(original.OutboundSubmissionId);
        round.ExternalSystemInstanceId.Should().Be(original.ExternalSystemInstanceId);
        round.IdempotencyKey.Should().Be(original.IdempotencyKey);
    }

    [Fact]
    public void OutboundSubmissionPayload_FutureUnknownField_IsIgnored()
    {
        // A future producer adds a field the current consumer does not
        // know about. Web defaults must ignore it (not throw).
        var json = """
        {
          "workItemId":             "11111111-1111-1111-1111-111111111111",
          "caseId":                 "22222222-2222-2222-2222-222222222222",
          "enqueuedAt":             "2026-05-13T10:00:00+00:00",
          "outboundSubmissionId":   "33333333-3333-3333-3333-333333333333",
          "futureRetryStrategy":    { "backoffSeconds": 30, "jitter": 0.25 }
        }
        """;

        var act = () => JsonSerializer.Deserialize<OutboundSubmissionPayload>(json, WebOptions);

        act.Should().NotThrow(
            because: "forward compat — unknown fields must be ignored under JsonSerializerDefaults.Web");
        var payload = act();
        payload!.OutboundSubmissionId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public void AuditReviewPayload_LegacyJson_DeserializesWithNullExtensionFields()
    {
        // Shape stored before the ReviewId + Outcome fields were added.
        var legacyJson = """
        {
          "workItemId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
          "caseId":     "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
          "enqueuedAt": "2026-05-13T10:00:00+00:00"
        }
        """;

        var payload = JsonSerializer.Deserialize<AuditReviewPayload>(legacyJson, WebOptions);

        payload.Should().NotBeNull();
        payload!.WorkItemId.Should().Be(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        payload.CaseId.Should().Be(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        payload.ReviewId.Should().BeNull(
            because: "legacy rows predate the ReviewId carry-through (commit f1e1767d)");
        payload.Outcome.Should().BeNull();
    }

    [Fact]
    public void AuditReviewPayload_NewJson_RoundTrips()
    {
        var original = new AuditReviewPayload(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow)
        {
            ReviewId = Guid.NewGuid(),
            Outcome = "concur"
        };

        var json = JsonSerializer.Serialize(original, WebOptions);
        var round = JsonSerializer.Deserialize<AuditReviewPayload>(json, WebOptions);

        round.Should().NotBeNull();
        round!.ReviewId.Should().Be(original.ReviewId);
        round.Outcome.Should().Be(original.Outcome);
    }

    [Fact]
    public void AuditReviewPayload_FutureUnknownField_IsIgnored()
    {
        var json = """
        {
          "workItemId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
          "caseId":     "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
          "enqueuedAt": "2026-05-13T10:00:00+00:00",
          "reviewId":   "cccccccc-cccc-cccc-cccc-cccccccccccc",
          "outcome":    "concur",
          "secondOpinion": { "requested": true, "by": "lead-analyst" }
        }
        """;

        var act = () => JsonSerializer.Deserialize<AuditReviewPayload>(json, WebOptions);

        act.Should().NotThrow();
        var payload = act();
        payload!.Outcome.Should().Be("concur");
        payload.ReviewId.Should().Be(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
    }
}
