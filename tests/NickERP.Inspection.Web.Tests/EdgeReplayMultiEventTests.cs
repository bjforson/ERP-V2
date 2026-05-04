using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Web.Endpoints;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 17 / P2-FU-multi-event-types — exercises the per-hint
/// dispatch in <see cref="EdgeReplayEndpoint"/>. Each new hint
/// (<see cref="EdgeReplayEndpoint.ScanCapturedHint"/> +
/// <see cref="EdgeReplayEndpoint.ScannerStatusChangedHint"/>) gets:
/// (1) a happy-path round-trip test that verifies the audit row
///     metadata + payload preservation;
/// (2) a malformed-payload-per-entry-error test;
/// (3) a single mixed-batch test that confirms the three hints fan
///     out to different audit rows in one /api/edge/replay call.
/// The Sprint 11 audit-event-replay coverage in
/// <see cref="EdgeReplayEndpointTests"/> is unchanged.
/// </summary>
public sealed class EdgeReplayMultiEventTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScanCaptured_writes_audit_row_with_fixed_eventType_and_artifact_entityId()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("edge-scan-ok");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-tema-1",
            TenantId = 17,
            AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, EdgeReplayEndpointTests.BuildConfig());

        var edgeStamp = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);
        var body = new EdgeReplayRequestDto("edge-tema-1", new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScanCapturedHint, 17, edgeStamp,
                JsonSerializer.SerializeToElement(new
                {
                    scannerId = "fs6000-tema-1",
                    locationId = "11111111-1111-1111-1111-111111111111",
                    sourcePath = "/scans/2026-05-04/MSCU1234567",
                    subjectIdentifier = "MSCU1234567",
                    correlationId = "trace-aa"
                }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeTrue();

        var row = await db.Events.AsNoTracking().SingleAsync();
        row.TenantId.Should().Be(17);
        row.EventType.Should().Be("inspection.scan.captured");
        row.EntityType.Should().Be("ScanArtifact");
        row.EntityId.Should().Be("/scans/2026-05-04/MSCU1234567");
        row.OccurredAt.Should().Be(edgeStamp);
        row.CorrelationId.Should().Be("trace-aa");

        // Original payload fields preserved alongside replay metadata.
        using var doc = JsonDocument.Parse(row.Payload.RootElement.GetRawText());
        doc.RootElement.GetProperty("scannerId").GetString().Should().Be("fs6000-tema-1");
        doc.RootElement.GetProperty("subjectIdentifier").GetString().Should().Be("MSCU1234567");
        doc.RootElement.GetProperty("replay_source").GetString().Should().Be("edge");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScanCaptured_with_missing_scannerId_yields_per_entry_error()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("edge-scan-bad");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, EdgeReplayEndpointTests.BuildConfig());

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScanCapturedHint, 17, DateTimeOffset.UtcNow.AddMinutes(-1),
                JsonSerializer.SerializeToElement(new { sourcePath = "/p" }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("scan-captured payload missing required fields");

        (await db.Events.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScannerStatusChanged_writes_audit_row_with_fixed_eventType_and_scanner_entityId()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("edge-status-ok");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-tema-1",
            TenantId = 17,
            AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, EdgeReplayEndpointTests.BuildConfig());

        var edgeStamp = new DateTimeOffset(2026, 5, 4, 10, 30, 0, TimeSpan.Zero);
        var body = new EdgeReplayRequestDto("edge-tema-1", new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScannerStatusChangedHint, 17, edgeStamp,
                JsonSerializer.SerializeToElement(new
                {
                    scannerId = "fs6000-tema-1",
                    status = "online",
                    statusDetail = "warmed-up after 35min idle"
                }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeTrue();

        var row = await db.Events.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("inspection.scanner.status.changed");
        row.EntityType.Should().Be("ScannerDeviceInstance");
        row.EntityId.Should().Be("fs6000-tema-1");

        using var doc = JsonDocument.Parse(row.Payload.RootElement.GetRawText());
        doc.RootElement.GetProperty("status").GetString().Should().Be("online");
        doc.RootElement.GetProperty("statusDetail").GetString().Should().Be("warmed-up after 35min idle");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ScannerStatusChanged_with_missing_status_yields_per_entry_error()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("edge-status-bad");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, EdgeReplayEndpointTests.BuildConfig());

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScannerStatusChangedHint, 17, DateTimeOffset.UtcNow.AddMinutes(-1),
                JsonSerializer.SerializeToElement(new { scannerId = "scn" }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("scanner-status-changed payload missing required fields");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Mixed_batch_with_three_event_types_writes_three_distinct_audit_rows()
    {
        await using var db = EdgeReplayEndpointTests.BuildDb("edge-mixed-3");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-tema-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, EdgeReplayEndpointTests.BuildConfig());
        var stamp = new DateTimeOffset(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

        var body = new EdgeReplayRequestDto("edge-tema-1", new List<EdgeReplayEventDto>
        {
            // 1) audit event (Sprint 11)
            new(EdgeReplayEndpoint.AuditEventReplayHint, 17, stamp,
                JsonSerializer.SerializeToElement(new
                {
                    eventType = "inspection.case.opened",
                    entityType = "InspectionCase",
                    entityId = "case-abc"
                })),
            // 2) scan-captured (Sprint 17)
            new(EdgeReplayEndpoint.ScanCapturedHint, 17, stamp.AddSeconds(1),
                JsonSerializer.SerializeToElement(new
                {
                    scannerId = "fs6000-tema-1",
                    locationId = "loc-1",
                    sourcePath = "/scans/abc"
                })),
            // 3) scanner-status-changed (Sprint 17)
            new(EdgeReplayEndpoint.ScannerStatusChangedHint, 17, stamp.AddSeconds(2),
                JsonSerializer.SerializeToElement(new
                {
                    scannerId = "fs6000-tema-1",
                    status = "idle"
                }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Should().HaveCount(3);
        dto.Results.Should().AllSatisfy(r => r.Ok.Should().BeTrue());

        var rows = await db.Events.AsNoTracking().OrderBy(e => e.OccurredAt).ToListAsync();
        rows.Should().HaveCount(3);

        rows[0].EventType.Should().Be("inspection.case.opened");
        rows[0].EntityType.Should().Be("InspectionCase");
        rows[0].EntityId.Should().Be("case-abc");

        rows[1].EventType.Should().Be("inspection.scan.captured");
        rows[1].EntityType.Should().Be("ScanArtifact");
        rows[1].EntityId.Should().Be("/scans/abc");

        rows[2].EventType.Should().Be("inspection.scanner.status.changed");
        rows[2].EntityType.Should().Be("ScannerDeviceInstance");
        rows[2].EntityId.Should().Be("fs6000-tema-1");

        // Single batch summary with three OK entries.
        var summary = await db.EdgeNodeReplayLogs.AsNoTracking().SingleAsync();
        summary.EventCount.Should().Be(3);
        summary.OkCount.Should().Be(3);
        summary.FailedCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Unsupported_hint_lists_all_three_supported_hints_in_error()
    {
        // Sprint 17 — the rejection message should enumerate all three
        // supported hints so an edge node operator running an old
        // build can see what changed.
        await using var db = EdgeReplayEndpointTests.BuildDb("edge-bad-hint-17");
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = "edge-1", TenantId = 17, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = NewAuthedHttp();
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(db, EdgeReplayEndpointTests.BuildConfig());

        var body = new EdgeReplayRequestDto("edge-1", new List<EdgeReplayEventDto>
        {
            new("voucher.disbursed", 17, DateTimeOffset.UtcNow.AddMinutes(-1),
                JsonSerializer.SerializeToElement(new { x = 1 }))
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, db, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        var err = dto.Results.Single().Error!;
        err.Should().Contain("unsupported eventTypeHint");
        err.Should().Contain("audit.event.replay");
        err.Should().Contain("inspection.scan.captured");
        err.Should().Contain("inspection.scanner.status.changed");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveAuditMetadata_returns_null_for_unknown_hint()
    {
        var meta = EdgeReplayEndpoint.ResolveAuditMetadata(
            "voucher.disbursed",
            JsonSerializer.SerializeToElement(new { x = 1 }));
        meta.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolveAuditMetadata_returns_failure_meta_on_malformed_payload()
    {
        var meta = EdgeReplayEndpoint.ResolveAuditMetadata(
            EdgeReplayEndpoint.ScanCapturedHint,
            JsonSerializer.SerializeToElement(new { sourcePath = "/p" }));
        meta.Should().NotBeNull();
        meta!.Value.Error.Should().Contain("scan-captured payload missing required fields");
    }

    private static DefaultHttpContext NewAuthedHttp()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Token"] = "test-secret";
        return http;
    }
}
