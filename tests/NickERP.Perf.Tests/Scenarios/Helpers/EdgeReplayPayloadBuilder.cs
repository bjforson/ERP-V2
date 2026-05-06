using System.Text.Json;
using System.Text.Json.Nodes;

namespace NickERP.Perf.Tests.Scenarios.Helpers;

/// <summary>
/// Sprint 55 — builds buffer-fixture payloads for the EdgeReplay perf
/// scenario. Mirrors the wire shape <c>EdgeReplayRequestDto</c> +
/// <c>EdgeReplayEventDto</c> uses (per Sprint 11 / 17 / 45 in
/// <c>EdgeReplayEndpoint.cs</c>): one batch envelope carrying 1-N event
/// entries, each with a Sprint 17 event-type hint
/// (<c>audit.event.replay</c>, <c>inspection.scan.captured</c>,
/// <c>inspection.scanner.status.changed</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why mixed hints.</b> Per Sprint 17 the replay endpoint is a
/// per-EventTypeHint dispatcher. The perf scenario must exercise every
/// hint to catch latency regressions in any single dispatch path
/// (per test-plan §5).
/// </para>
/// <para>
/// <b>Payload size envelope.</b> Per test-plan §5, per-flush size is
/// 5KB-50KB. Audit-replay payloads carry the load-bearing
/// <c>{eventType, entityType, entityId, payload}</c> shape; scan-
/// captured carries scanner+location+sourcePath; status-changed carries
/// scannerId+status. The shape stays stable so the deterministic
/// idempotency key path runs on every request.
/// </para>
/// <para>
/// <b>Per-event distribution.</b> 60% audit-replay (the hot dispatch
/// path), 30% scan-captured (the Sprint 17 happy path), 10% status-
/// changed (the rare-but-must-work path). Mirrors what an edge node
/// would replay in real traffic.
/// </para>
/// </remarks>
public static class EdgeReplayPayloadBuilder
{
    /// <summary>The three event-type hints the endpoint understands.</summary>
    public const string AuditEventReplayHint = "audit.event.replay";
    public const string ScanCapturedHint = "inspection.scan.captured";
    public const string ScannerStatusChangedHint = "inspection.scanner.status.changed";

    /// <summary>
    /// Build a single batch envelope. <paramref name="rng"/> drives both
    /// the event-count distribution and the per-event hint mix. The
    /// resulting JSON string is ready for an HTTP body.
    /// </summary>
    /// <param name="rng">RNG for varying batches across iterations.</param>
    /// <param name="edgeNodeId">Stable edge node id for this run.</param>
    /// <param name="tenantId">Tenant id all events in this batch belong to (matches the per-edge authorisation).</param>
    /// <param name="meanEvents">Mean events per batch (per test-plan §5; default 5).</param>
    /// <param name="maxEvents">Max events per batch (default 20).</param>
    public static string BuildBatch(
        Random rng,
        string edgeNodeId,
        long tenantId,
        int meanEvents = 5,
        int maxEvents = 20)
    {
        ArgumentNullException.ThrowIfNull(rng);
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeNodeId);

        // Pick batch size around the mean; clamp to [1, max].
        var size = Math.Clamp(
            (int)Math.Round(meanEvents + (rng.NextDouble() - 0.5) * meanEvents),
            1, maxEvents);

        var events = new JsonArray();
        for (var i = 0; i < size; i++)
        {
            events.Add(BuildEvent(rng, tenantId));
        }

        var envelope = new JsonObject
        {
            ["edgeNodeId"] = edgeNodeId,
            ["events"] = events
        };

        return envelope.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// Build one event entry — picks a hint by the per-event
    /// distribution and shapes the payload accordingly. Public so unit
    /// tests can verify the per-hint payload shape.
    /// </summary>
    public static JsonObject BuildEvent(Random rng, long tenantId)
    {
        ArgumentNullException.ThrowIfNull(rng);

        var roll = rng.Next(0, 100);
        // 60% audit-replay, 30% scan-captured, 10% status-changed.
        var hint = roll switch
        {
            < 60 => AuditEventReplayHint,
            < 90 => ScanCapturedHint,
            _ => ScannerStatusChangedHint
        };

        var edgeTimestamp = DateTimeOffset.UtcNow.AddSeconds(-rng.Next(1, 1800));

        return new JsonObject
        {
            ["eventTypeHint"] = hint,
            ["tenantId"] = tenantId,
            ["edgeTimestamp"] = edgeTimestamp.ToString("O"),
            ["payload"] = hint switch
            {
                AuditEventReplayHint => BuildAuditReplayPayload(rng),
                ScanCapturedHint => BuildScanCapturedPayload(rng),
                ScannerStatusChangedHint => BuildScannerStatusChangedPayload(rng),
                _ => new JsonObject()
            }
        };
    }

    /// <summary>
    /// Sprint 11 audit-event-replay payload. Maps to
    /// <c>EdgeReplayEndpoint.AuditEventReplayPayload</c>: must carry
    /// <c>eventType</c>, <c>entityType</c>, <c>entityId</c>;
    /// <c>actorUserId</c> and <c>correlationId</c> are optional.
    /// </summary>
    public static JsonObject BuildAuditReplayPayload(Random rng)
    {
        var entityId = Guid.NewGuid().ToString();
        return new JsonObject
        {
            ["eventType"] = "nickerp.inspection.case_opened",
            ["entityType"] = "InspectionCase",
            ["entityId"] = entityId,
            ["actorUserId"] = Guid.NewGuid().ToString(),
            ["correlationId"] = $"perf-edge-{rng.Next(1, 9999):D4}",
            ["caseId"] = entityId,
            ["subjectIdentifier"] = ContainerNumberGenerator.Generate(rng)
        };
    }

    /// <summary>
    /// Sprint 17 scan-captured payload. Maps to
    /// <c>EdgeReplayEndpoint.ScanCapturedPayload</c>: must carry
    /// <c>scannerId</c> + <c>sourcePath</c>; <c>locationId</c>,
    /// <c>subjectIdentifier</c> are optional.
    /// </summary>
    public static JsonObject BuildScanCapturedPayload(Random rng)
    {
        var scannerId = $"perf-scanner-{rng.Next(1, 99):D2}";
        var sourcePath = $"perf://edge/{Guid.NewGuid():N}/scan.png";
        return new JsonObject
        {
            ["scannerId"] = scannerId,
            ["locationId"] = $"perf-loc-{rng.Next(1, 9):D1}",
            ["sourcePath"] = sourcePath,
            ["subjectIdentifier"] = ContainerNumberGenerator.Generate(rng),
            ["correlationId"] = $"perf-scan-{rng.Next(1, 9999):D4}"
        };
    }

    /// <summary>
    /// Sprint 17 scanner-status-changed payload. Maps to
    /// <c>EdgeReplayEndpoint.ScannerStatusChangedPayload</c>: must
    /// carry <c>scannerId</c> + <c>status</c>; <c>statusDetail</c>
    /// optional.
    /// </summary>
    public static JsonObject BuildScannerStatusChangedPayload(Random rng)
    {
        var scannerId = $"perf-scanner-{rng.Next(1, 99):D2}";
        var status = rng.Next(0, 3) switch
        {
            0 => "online",
            1 => "idle",
            _ => "error"
        };
        return new JsonObject
        {
            ["scannerId"] = scannerId,
            ["status"] = status,
            ["statusDetail"] = status == "error" ? "perf-test simulated error" : null,
            ["correlationId"] = $"perf-status-{rng.Next(1, 9999):D4}"
        };
    }

    /// <summary>
    /// JSON serialisation options matching the API endpoint's web
    /// defaults (camelCase). Public so unit tests can re-serialise.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
