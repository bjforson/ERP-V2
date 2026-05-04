using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.EdgeNode;

namespace NickERP.EdgeNode.Tests;

/// <summary>
/// Sprint 17 / P2-FU-multi-event-types — exercises the typed edge
/// capture helpers <see cref="IEdgeEventCapture.CaptureScanCapturedAsync"/>
/// + <see cref="IEdgeEventCapture.CaptureScannerStatusChangedAsync"/>
/// + <see cref="IEdgeEventCapture.CaptureAuditEventAsync"/>. Confirms
/// each writes the right hint + the payload deserialises into the
/// expected wire shape.
/// </summary>
public sealed class EdgeMultiEventCaptureTests : IAsyncLifetime
{
    private SqliteEdgeBufferFixture _fx = null!;

    public async Task InitializeAsync() => _fx = await SqliteEdgeBufferFixture.CreateAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    private EdgeEventCapture NewCapture()
    {
        return new EdgeEventCapture(
            _fx.Db,
            Options.Create(new EdgeNodeOptions { Id = "edge-test-17" }),
            NullLogger<EdgeEventCapture>.Instance,
            new FixedClock(new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CaptureAuditEventAsync_writes_audit_event_replay_hint()
    {
        var capture = NewCapture();

        var id = await capture.CaptureAuditEventAsync(
            tenantId: 17,
            payload: new { eventType = "x.y", entityType = "T", entityId = "1" });

        var row = await _fx.Db.Outbox.AsNoTracking().SingleAsync(o => o.Id == id);
        row.EventTypeHint.Should().Be(EdgeEventTypes.AuditEventReplay);
        row.EventTypeHint.Should().Be("audit.event.replay");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CaptureScanCapturedAsync_writes_scan_captured_hint_and_typed_payload()
    {
        var capture = NewCapture();
        var payload = new EdgeScanCaptured(
            ScannerId: "fs6000-tema-1",
            LocationId: Guid.Parse("11111111-1111-1111-1111-111111111111").ToString(),
            SourcePath: "/scans/2026-05-04/abc123",
            SubjectIdentifier: "MSCU1234567",
            CorrelationId: "trace-ab");

        var id = await capture.CaptureScanCapturedAsync(17, payload);

        var row = await _fx.Db.Outbox.AsNoTracking().SingleAsync(o => o.Id == id);
        row.EventTypeHint.Should().Be(EdgeEventTypes.ScanCaptured);
        row.EventTypeHint.Should().Be("inspection.scan.captured");

        // Payload is camelCase JSON (Web defaults).
        using var doc = JsonDocument.Parse(row.EventPayloadJson);
        doc.RootElement.GetProperty("scannerId").GetString().Should().Be("fs6000-tema-1");
        doc.RootElement.GetProperty("sourcePath").GetString().Should().Be("/scans/2026-05-04/abc123");
        doc.RootElement.GetProperty("subjectIdentifier").GetString().Should().Be("MSCU1234567");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CaptureScanCapturedAsync_rejects_missing_scannerId()
    {
        var capture = NewCapture();
        var payload = new EdgeScanCaptured(
            ScannerId: "",
            LocationId: "loc",
            SourcePath: "/path");
        var act = () => capture.CaptureScanCapturedAsync(17, payload);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*scannerId*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CaptureScanCapturedAsync_rejects_missing_sourcePath()
    {
        var capture = NewCapture();
        var payload = new EdgeScanCaptured(
            ScannerId: "fs6000-tema-1",
            LocationId: "loc",
            SourcePath: "");
        var act = () => capture.CaptureScanCapturedAsync(17, payload);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*sourcePath*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CaptureScannerStatusChangedAsync_writes_status_changed_hint()
    {
        var capture = NewCapture();
        var payload = new EdgeScannerStatusChanged(
            ScannerId: "fs6000-tema-1",
            Status: "online",
            StatusDetail: "warmed-up");

        var id = await capture.CaptureScannerStatusChangedAsync(17, payload);

        var row = await _fx.Db.Outbox.AsNoTracking().SingleAsync(o => o.Id == id);
        row.EventTypeHint.Should().Be(EdgeEventTypes.ScannerStatusChanged);
        row.EventTypeHint.Should().Be("inspection.scanner.status.changed");

        using var doc = JsonDocument.Parse(row.EventPayloadJson);
        doc.RootElement.GetProperty("scannerId").GetString().Should().Be("fs6000-tema-1");
        doc.RootElement.GetProperty("status").GetString().Should().Be("online");
        doc.RootElement.GetProperty("statusDetail").GetString().Should().Be("warmed-up");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task CaptureScannerStatusChangedAsync_rejects_missing_status()
    {
        var capture = NewCapture();
        var payload = new EdgeScannerStatusChanged(
            ScannerId: "fs6000-tema-1",
            Status: "");
        var act = () => capture.CaptureScannerStatusChangedAsync(17, payload);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*status*");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Three_event_types_in_FIFO_order_drain_in_one_batch()
    {
        var capture = NewCapture();

        var auditId = await capture.CaptureAuditEventAsync(
            17, new { eventType = "x", entityType = "T", entityId = "a" });
        var scanId = await capture.CaptureScanCapturedAsync(
            17, new EdgeScanCaptured("scn-1", "loc-1", "/p"));
        var statusId = await capture.CaptureScannerStatusChangedAsync(
            17, new EdgeScannerStatusChanged("scn-1", "idle"));

        var rows = await _fx.Db.Outbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
        rows.Should().HaveCount(3);
        rows[0].Id.Should().Be(auditId);
        rows[1].Id.Should().Be(scanId);
        rows[2].Id.Should().Be(statusId);

        rows.Select(r => r.EventTypeHint).Should().Equal(
            EdgeEventTypes.AuditEventReplay,
            EdgeEventTypes.ScanCaptured,
            EdgeEventTypes.ScannerStatusChanged);
    }
}
