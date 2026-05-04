using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NickERP.EdgeNode;

/// <summary>
/// Sprint 11 / P2 + Sprint 17 / P2-FU-multi-event-types — surface
/// adapter code calls to enqueue an event into the edge node's local
/// SQLite buffer. The single seam between "something happened on the
/// edge" and the offline-safe write store.
///
/// <para>
/// Always treats the capture as offline-first: writes locally and
/// returns. The actual replay to the central server is the
/// <see cref="EdgeReplayWorker"/>'s problem; capture never fails on
/// network.
/// </para>
///
/// <para>
/// **Sprint 17 — three event types supported.** Use the most specific
/// helper available so the payload shape stays consistent with what
/// the server's <c>EdgeReplayEndpoint.ResolveAuditMetadata</c>
/// expects:
/// <list type="bullet">
///   <item><description><see cref="CaptureAuditEventAsync"/> — Sprint 11 path. Payload IS the <c>DomainEvent</c> shape.</description></item>
///   <item><description><see cref="CaptureScanCapturedAsync"/> — Sprint 17. Wraps the strongly-typed scan-captured payload.</description></item>
///   <item><description><see cref="CaptureScannerStatusChangedAsync"/> — Sprint 17. Wraps the strongly-typed status-changed payload.</description></item>
/// </list>
/// The original <see cref="CaptureAsync"/> stays as the low-level
/// escape hatch for forward-compatibility (a fourth hint can be
/// captured without a library upgrade, as long as the server's
/// dispatcher knows it).
/// </para>
/// </summary>
public interface IEdgeEventCapture
{
    /// <summary>
    /// Enqueue an event to the local <c>edge_outbox</c>. Stamps
    /// <c>EdgeTimestamp = DateTimeOffset.UtcNow</c> and the configured
    /// <c>EdgeNode:Id</c>; serialises <paramref name="payload"/> via
    /// <see cref="System.Text.Json.JsonSerializer"/>.
    /// </summary>
    /// <param name="eventTypeHint">
    ///   Discriminator the server uses to pick a replay handler. The
    ///   call site is responsible for shaping the payload to match
    ///   what the server's dispatcher expects for that hint. Prefer
    ///   the typed helpers below.
    /// </param>
    /// <param name="tenantId">Tenant the captured event belongs to.</param>
    /// <param name="payload">JSON-serialisable object — the event body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The id assigned to the new <see cref="EdgeOutboxEntry"/>.</returns>
    Task<long> CaptureAsync(
        string eventTypeHint,
        long tenantId,
        object payload,
        CancellationToken ct = default);

    /// <summary>
    /// Sprint 11 path — enqueue a domain audit event. Payload should
    /// have <c>eventType</c>/<c>entityType</c>/<c>entityId</c> at
    /// minimum (see <c>EdgeReplayEndpoint.AuditEventReplayPayload</c>
    /// on the server side). Hint = <see cref="EdgeEventTypes.AuditEventReplay"/>.
    /// </summary>
    Task<long> CaptureAuditEventAsync(
        long tenantId,
        object payload,
        CancellationToken ct = default);

    /// <summary>
    /// Sprint 17 — enqueue a scan-captured event. The server records
    /// it in <c>audit.events</c> with
    /// <c>EventType = "inspection.scan.captured"</c>; an inspection-
    /// side projector consumes the audit rows in a future sprint to
    /// drive the actual case workflow ingestion. Hint =
    /// <see cref="EdgeEventTypes.ScanCaptured"/>.
    /// </summary>
    Task<long> CaptureScanCapturedAsync(
        long tenantId,
        EdgeScanCaptured payload,
        CancellationToken ct = default);

    /// <summary>
    /// Sprint 17 — enqueue a scanner-status-changed event. The server
    /// records it in <c>audit.events</c> with
    /// <c>EventType = "inspection.scanner.status.changed"</c>; an
    /// inspection-side projector consumes the audit rows in a future
    /// sprint to maintain a per-scanner latest-state lookup. Hint =
    /// <see cref="EdgeEventTypes.ScannerStatusChanged"/>.
    /// </summary>
    Task<long> CaptureScannerStatusChangedAsync(
        long tenantId,
        EdgeScannerStatusChanged payload,
        CancellationToken ct = default);
}

/// <summary>
/// Sprint 17 — canonical event-type-hint constants shared by edge
/// adapters + tests. Server side uses the same strings via
/// <c>EdgeReplayEndpoint.AuditEventReplayHint</c> /
/// <c>ScanCapturedHint</c> / <c>ScannerStatusChangedHint</c>.
/// </summary>
public static class EdgeEventTypes
{
    /// <summary>Hint for Sprint 11 audit-event replay. Payload is a <c>DomainEvent</c>-shape.</summary>
    public const string AuditEventReplay = "audit.event.replay";

    /// <summary>Sprint 17 — hint for raw scanner artifact captured at the edge.</summary>
    public const string ScanCaptured = "inspection.scan.captured";

    /// <summary>Sprint 17 — hint for scanner device status change captured at the edge.</summary>
    public const string ScannerStatusChanged = "inspection.scanner.status.changed";
}

/// <summary>
/// Sprint 17 — typed payload for
/// <see cref="IEdgeEventCapture.CaptureScanCapturedAsync"/>. Maps
/// 1:1 to the server's <c>EdgeReplayEndpoint.ScanCapturedPayload</c>.
/// </summary>
public sealed record EdgeScanCaptured(
    string ScannerId,
    string LocationId,
    string SourcePath,
    string? SubjectIdentifier = null,
    Guid? ActorUserId = null,
    string? CorrelationId = null);

/// <summary>
/// Sprint 17 — typed payload for
/// <see cref="IEdgeEventCapture.CaptureScannerStatusChangedAsync"/>.
/// Maps 1:1 to the server's
/// <c>EdgeReplayEndpoint.ScannerStatusChangedPayload</c>.
/// </summary>
public sealed record EdgeScannerStatusChanged(
    string ScannerId,
    string Status,
    string? StatusDetail = null,
    Guid? ActorUserId = null,
    string? CorrelationId = null);

/// <summary>
/// Default <see cref="IEdgeEventCapture"/> — writes one
/// <see cref="EdgeOutboxEntry"/> per call. Scoped (captures the
/// per-request <see cref="EdgeBufferDbContext"/> and clock).
/// </summary>
public sealed class EdgeEventCapture : IEdgeEventCapture
{
    private readonly EdgeBufferDbContext _db;
    private readonly IOptions<EdgeNodeOptions> _opts;
    private readonly TimeProvider _clock;
    private readonly ILogger<EdgeEventCapture> _logger;

    public EdgeEventCapture(
        EdgeBufferDbContext db,
        IOptions<EdgeNodeOptions> opts,
        ILogger<EdgeEventCapture> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<long> CaptureAsync(
        string eventTypeHint,
        long tenantId,
        object payload,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventTypeHint);
        ArgumentNullException.ThrowIfNull(payload);
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "tenantId must be positive.");

        var edgeNodeId = _opts.Value.Id;
        if (string.IsNullOrWhiteSpace(edgeNodeId))
            throw new InvalidOperationException(
                "EdgeNode:Id is not configured. Set it in appsettings.json before capturing events.");

        var entry = new EdgeOutboxEntry
        {
            EventPayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            EventTypeHint = eventTypeHint,
            EdgeTimestamp = _clock.GetUtcNow(),
            EdgeNodeId = edgeNodeId,
            TenantId = tenantId,
            ReplayedAt = null,
            ReplayAttempts = 0,
            LastReplayError = null
        };

        _db.Outbox.Add(entry);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Captured edge event id={Id} hint={Hint} tenant={Tenant} (queue grows)",
            entry.Id, eventTypeHint, tenantId);

        return entry.Id;
    }

    /// <inheritdoc />
    public Task<long> CaptureAuditEventAsync(
        long tenantId, object payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return CaptureAsync(EdgeEventTypes.AuditEventReplay, tenantId, payload, ct);
    }

    /// <inheritdoc />
    public Task<long> CaptureScanCapturedAsync(
        long tenantId, EdgeScanCaptured payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.ScannerId))
            throw new ArgumentException("scannerId is required.", nameof(payload));
        if (string.IsNullOrWhiteSpace(payload.SourcePath))
            throw new ArgumentException("sourcePath is required.", nameof(payload));
        return CaptureAsync(EdgeEventTypes.ScanCaptured, tenantId, payload, ct);
    }

    /// <inheritdoc />
    public Task<long> CaptureScannerStatusChangedAsync(
        long tenantId, EdgeScannerStatusChanged payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrWhiteSpace(payload.ScannerId))
            throw new ArgumentException("scannerId is required.", nameof(payload));
        if (string.IsNullOrWhiteSpace(payload.Status))
            throw new ArgumentException("status is required.", nameof(payload));
        return CaptureAsync(EdgeEventTypes.ScannerStatusChanged, tenantId, payload, ct);
    }

    /// <summary>
    /// Sprint 17 — JSON serialiser options used for outbox payloads.
    /// Web-defaults (camelCase) so the server's
    /// <c>EdgeReplayEndpoint.ResolveAuditMetadata</c> can deserialise
    /// the typed payload records (which use PascalCase property names)
    /// into the server-side records that also use PascalCase. Both
    /// sides use <c>JsonSerializerDefaults.Web</c>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}
