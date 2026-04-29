using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NickERP.EdgeNode;

/// <summary>
/// Sprint 11 / P2 — surface adapter code calls to enqueue an event into
/// the edge node's local SQLite buffer. The single seam between
/// "something happened on the edge" and the offline-safe write store.
///
/// <para>
/// Always treats the capture as offline-first: writes locally and
/// returns. The actual replay to the central server is the
/// <see cref="EdgeReplayWorker"/>'s problem; capture never fails on
/// network.
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
    ///   Discriminator the server uses to pick a replay handler. v0
    ///   only handles <c>"audit.event.replay"</c>; the call site is
    ///   responsible for shaping the payload to match.
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
}

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
            EventPayloadJson = JsonSerializer.Serialize(payload),
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
}
