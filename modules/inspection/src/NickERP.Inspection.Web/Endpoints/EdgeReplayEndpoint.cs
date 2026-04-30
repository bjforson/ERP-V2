using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Endpoints;

/// <summary>
/// Sprint 11 / P2 — server-side replay endpoint that accepts buffered
/// audit events from edge nodes and writes them into
/// <c>audit.events</c> as fresh appends.
///
/// <para>
/// <b>v0 scope.</b> Only <c>audit.event.replay</c> is supported as an
/// <c>eventTypeHint</c>; payloads are <c>DomainEvent</c>-shaped and
/// the server writes each one verbatim to <c>audit.events</c>. The
/// edge-captured <c>edgeTimestamp</c> becomes <c>OccurredAt</c>; the
/// server's wall-clock at replay-time becomes <c>IngestedAt</c>; the
/// audit row's <c>Payload</c> jsonb is augmented with replay metadata
/// (<c>replay_source</c>, <c>replay_node_id</c>, <c>replayed_at</c>).
/// Future event types (scan-captured, voucher-disbursed) are a
/// follow-up sprint; the mechanism here stays the same.
/// </para>
///
/// <para>
/// <b>Auth posture.</b> Anonymous endpoint gated by
/// <see cref="EdgeAuthHandler"/>, which prefers a per-edge-node API
/// key in the <c>X-Edge-Api-Key</c> header (or <c>Authorization:
/// Bearer &lt;key&gt;</c>) and falls back to the legacy Sprint 11
/// <c>X-Edge-Token</c> shared secret while
/// <c>EdgeAuth:AllowLegacyToken</c> is truthy in config. Per-node
/// keys are HMAC-hashed at rest, individually revocable, and
/// optionally expirable — a strict strengthening over the Sprint 11
/// single-shared-secret flow. Once every edge has migrated to a
/// per-node key, ops should flip <c>EdgeAuth:AllowLegacyToken=false</c>
/// to retire the legacy path entirely.
/// </para>
///
/// <para>
/// <b>System-context caller.</b> The endpoint writes
/// <c>audit.events</c> on behalf of multiple tenants in one batch,
/// so it calls <see cref="ITenantContext.SetSystemContext"/> for the
/// duration of the per-batch processing. This is registered in
/// <c>docs/system-context-audit-register.md</c>; the Sprint 5 opt-in
/// clause on <c>audit.events</c> already admits the writes (the
/// policy's WITH CHECK has the <c>OR app.tenant_id = '-1'</c>
/// clause). No new opt-in is needed.
/// </para>
///
/// <para>
/// <b>Idempotency.</b> A replay batch can be retried by the edge
/// without producing duplicate audit rows. The dedup key is the
/// existing <c>(TenantId, IdempotencyKey)</c> unique index on
/// <c>audit.events</c>; the server constructs the
/// <c>IdempotencyKey</c> deterministically from
/// <c>edge_node_id + edge_timestamp + sha256(payload)</c> so two
/// replays of the same edge row collapse to the same audit row. If
/// the edge replays the same batch twice, the second pass returns
/// <c>ok: true</c> for every entry (the existing audit row is the
/// idempotent winner) and writes a fresh <c>edge_node_replay_log</c>
/// summary so ops can see the retry happened.
/// </para>
/// </summary>
public static class EdgeReplayEndpoint
{
    /// <summary>HTTP header carrying the legacy shared edge token (Sprint 11; retired post-rollout).</summary>
    public const string TokenHeader = "X-Edge-Token";

    /// <summary>Configuration key for the legacy Sprint 11 shared secret. Read only when <c>EdgeAuth:AllowLegacyToken</c> is truthy.</summary>
    public const string SharedSecretConfigKey = "EdgeNode:SharedSecret";

    /// <summary>Stable hint string for v0 audit-event replay.</summary>
    public const string AuditEventReplayHint = "audit.event.replay";

    /// <summary>
    /// Map the edge replay endpoint onto <paramref name="app"/>.
    /// Wire from <c>Program.cs</c> after authentication; the endpoint
    /// itself is anonymous (<see cref="EdgeAuthHandler"/> gates).
    /// </summary>
    public static IEndpointRouteBuilder MapEdgeReplayEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/edge/replay", HandleAsync)
            .AllowAnonymous()
            .WithTags("Edge Node Replay");

        return app;
    }

    /// <summary>
    /// Handler. Public so unit tests can drive it without a full
    /// Kestrel host.
    /// </summary>
    public static async Task<IResult> HandleAsync(
        HttpContext http,
        AuditDbContext db,
        ITenantContext tenant,
        EdgeAuthHandler auth,
        ILoggerFactory loggerFactory,
        EdgeReplayRequestDto body,
        TimeProvider? clock = null,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("EdgeReplayEndpoint");

        // ---- 1. Auth: per-node API key (preferred) + legacy fallback.
        // The handler runs the lookup under SetSystemContext (pre-tenant-
        // resolution), so by the time we return here either we're
        // authenticated and the tenant context is in system mode (which
        // is what the rest of this handler also wants for cross-tenant
        // batches) or the request is rejected.
        var authResult = await auth.AuthenticateAsync(http, ct);
        if (authResult.Outcome is not EdgeAuthOutcome.AuthenticatedPerNode
                                  and not EdgeAuthOutcome.AuthenticatedLegacy)
        {
            logger.LogWarning("Edge replay rejected: outcome={Outcome}.", authResult.Outcome);
            return Results.Unauthorized();
        }

        // ---- 2. Validate the request envelope. -----------------------
        if (body is null
            || string.IsNullOrWhiteSpace(body.EdgeNodeId)
            || body.Events is null)
        {
            return Results.BadRequest(new { error = "edgeNodeId + events required." });
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var results = new List<EdgeReplayResultDto>(body.Events.Count);
        var failureMetas = new List<object>();

        // ---- 3. Process under system context. ------------------------
        // The endpoint accepts events from multiple tenants in one
        // batch; SetSystemContext is the only way to do that within a
        // single request scope. The Sprint 5 opt-in clause on
        // audit.events admits the per-tenant writes via the OR clause
        // on app.tenant_id = '-1'. Registered in
        // docs/system-context-audit-register.md.
        tenant.SetSystemContext();

        // Pre-fetch the authorization rows for this edge node. One
        // query, used to gate every event in the batch — far cheaper
        // than per-event lookups. Read under system context so the
        // suite-wide reference table is visible (it has no tenant
        // RLS but rows could be filtered by other policies in
        // future).
        var authorizedTenants = await db.EdgeNodeAuthorizations
            .Where(a => a.EdgeNodeId == body.EdgeNodeId)
            .Select(a => a.TenantId)
            .ToListAsync(ct);
        var authorizedSet = authorizedTenants.ToHashSet();

        var okCount = 0;
        var failedCount = 0;
        for (var i = 0; i < body.Events.Count; i++)
        {
            var evt = body.Events[i];
            var entryResult = await ProcessOneAsync(
                db, body.EdgeNodeId, evt, authorizedSet, now, logger, ct);
            results.Add(entryResult);
            if (entryResult.Ok)
            {
                okCount += 1;
            }
            else
            {
                failedCount += 1;
                failureMetas.Add(new { index = i, error = entryResult.Error ?? "unspecified" });
            }
        }

        // ---- 4. Write per-batch summary for ops visibility. ----------
        var summary = new EdgeNodeReplayLog
        {
            EdgeNodeId = body.EdgeNodeId,
            ReplayedAt = now,
            EventCount = body.Events.Count,
            OkCount = okCount,
            FailedCount = failedCount,
            FailuresJson = failureMetas.Count == 0
                ? null
                : JsonSerializer.Serialize(failureMetas)
        };
        db.EdgeNodeReplayLogs.Add(summary);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Persisting the summary should never fail in normal flow
            // (the summary table has no FK back to events; per-event
            // SaveChanges already happened inside ProcessOneAsync).
            // If it does, log loudly and let the response still
            // reflect the per-event results; the on-disk audit rows
            // are the source of truth.
            logger.LogError(ex, "Failed to write edge replay log summary; per-event audit rows already persisted.");
        }

        return Results.Ok(new EdgeReplayResponseDto(results));
    }

    /// <summary>
    /// Process one event in the batch. Returns the per-entry result
    /// and (on success) writes the audit row.
    /// </summary>
    private static async Task<EdgeReplayResultDto> ProcessOneAsync(
        AuditDbContext db,
        string edgeNodeId,
        EdgeReplayEventDto evt,
        HashSet<long> authorizedTenants,
        DateTimeOffset replayedAt,
        ILogger logger,
        CancellationToken ct)
    {
        // ---- 3a. v0 dispatcher: only audit.event.replay. -------------
        if (!string.Equals(evt.EventTypeHint, AuditEventReplayHint, StringComparison.Ordinal))
        {
            return new EdgeReplayResultDto(false,
                $"unsupported eventTypeHint '{evt.EventTypeHint}'. v0 supports only '{AuditEventReplayHint}'.");
        }

        // ---- 3b. Authorization. --------------------------------------
        if (!authorizedTenants.Contains(evt.TenantId))
        {
            return new EdgeReplayResultDto(false,
                $"tenant {evt.TenantId} not authorized for edge {edgeNodeId}");
        }

        // ---- 3c. Reject future-dated edge timestamps. ----------------
        // The spec carves these out: "Post events with future
        // timestamps (server rejects)." A small clock-skew tolerance
        // (60s) absorbs jittery NTP without admitting wildly wrong
        // values.
        if (evt.EdgeTimestamp > replayedAt.AddSeconds(60))
        {
            return new EdgeReplayResultDto(false,
                $"edge timestamp {evt.EdgeTimestamp:O} is more than 60s in the future relative to server time {replayedAt:O}");
        }

        // ---- 3d. Parse the audit-event payload. ----------------------
        // v0 expects the payload to be the DomainEvent-shape: at
        // minimum EventType, EntityType, EntityId, ActorUserId
        // (optional), and an inner Payload object. We parse defensively
        // — a malformed payload is a per-entry permanent error, not a
        // batch failure.
        AuditEventReplayPayload? parsed;
        try
        {
            parsed = evt.Payload.Deserialize<AuditEventReplayPayload>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return new EdgeReplayResultDto(false, $"invalid payload JSON: {ex.Message}");
        }
        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.EventType)
            || string.IsNullOrWhiteSpace(parsed.EntityType)
            || string.IsNullOrWhiteSpace(parsed.EntityId))
        {
            return new EdgeReplayResultDto(false,
                "payload missing required fields: eventType, entityType, entityId");
        }

        // ---- 3e. Build deterministic idempotency key. ----------------
        // edge_node_id + edge_timestamp + sha256(payload-bytes). Two
        // replays of the same edge row collapse to the same audit row
        // via the (TenantId, IdempotencyKey) unique index.
        var idempotencyKey = ComputeIdempotencyKey(edgeNodeId, evt.EdgeTimestamp, evt.Payload);

        // Already there? Treat as success — idempotent retry.
        var existing = await db.Events.AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.TenantId == evt.TenantId && e.IdempotencyKey == idempotencyKey,
                ct);
        if (existing is not null)
        {
            return new EdgeReplayResultDto(true, null);
        }

        // ---- 3f. Build the audit-row payload (with replay metadata). -
        // We re-serialise the original payload with three extra fields
        // injected at the top level: replay_source, replay_node_id,
        // replayed_at. Consumers can spot edge-replayed events by the
        // presence of replay_source.
        using var augmentedJson = AugmentPayload(evt.Payload, edgeNodeId, replayedAt);

        var row = new DomainEventRow
        {
            EventId = Guid.NewGuid(),
            TenantId = evt.TenantId,
            ActorUserId = parsed.ActorUserId,
            CorrelationId = parsed.CorrelationId,
            EventType = parsed.EventType!,
            EntityType = parsed.EntityType!,
            EntityId = parsed.EntityId!,
            Payload = augmentedJson,
            // Edge timestamp becomes OccurredAt — the canonical "when
            // did this happen" wall-clock. IngestedAt is the server's
            // wall-clock at replay time.
            OccurredAt = evt.EdgeTimestamp,
            IngestedAt = replayedAt,
            IdempotencyKey = idempotencyKey,
            PrevEventHash = null
        };

        try
        {
            db.Events.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Race — concurrent replay won. Re-fetch the winner and
            // succeed (idempotency).
            db.Entry(row).State = EntityState.Detached;
            var winner = await db.Events.AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.TenantId == evt.TenantId && e.IdempotencyKey == idempotencyKey,
                    ct);
            if (winner is not null)
            {
                return new EdgeReplayResultDto(true, null);
            }
            logger.LogError(ex, "Edge replay write failed for edge={Edge} key={Key}", edgeNodeId, idempotencyKey);
            return new EdgeReplayResultDto(false, $"persistence failure: {ex.Message}");
        }

        return new EdgeReplayResultDto(true, null);
    }

    /// <summary>
    /// Build the deterministic idempotency key as
    /// <c>edge_node_id|edge_timestamp_ticks|sha256(payload-json)</c>.
    /// SHA-256 keeps the key under the 128-char column limit while
    /// remaining content-addressed. Public for tests that want to
    /// pre-compute the key for assertions.
    /// </summary>
    public static string ComputeIdempotencyKey(string edgeNodeId, DateTimeOffset edgeTimestamp, JsonElement payload)
    {
        var canonical = JsonSerializer.Serialize(payload, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var hex = Convert.ToHexString(hash);
        // Bound the total length to 128 (column max). Edge ids are
        // <= 100 by entity config; plus the ticks string and `|`
        // separators, this stays comfortably under the limit by
        // truncating the hash to 32 hex chars (128 bits — still
        // collision-resistant for our scope).
        return $"edge|{edgeNodeId}|{edgeTimestamp.UtcTicks}|{hex.AsSpan(0, 32)}";
    }

    /// <summary>
    /// Inject <c>replay_source</c>, <c>replay_node_id</c>,
    /// <c>replayed_at</c> into the top level of the original payload
    /// JSON. Returns a fresh <see cref="JsonDocument"/> the caller
    /// owns (and disposes via the <c>using</c> in the call site, or
    /// by EF when the row is materialised).
    /// </summary>
    private static JsonDocument AugmentPayload(JsonElement original, string edgeNodeId, DateTimeOffset replayedAt)
    {
        // If the original isn't an object we can't merge; wrap it.
        var bufferStream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(bufferStream))
        {
            writer.WriteStartObject();

            if (original.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in original.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }
            }
            else
            {
                writer.WritePropertyName("original");
                original.WriteTo(writer);
            }

            writer.WriteString("replay_source", "edge");
            writer.WriteString("replay_node_id", edgeNodeId);
            writer.WriteString("replayed_at", replayedAt.ToString("O"));
            writer.WriteEndObject();
        }
        bufferStream.Position = 0;
        return JsonDocument.Parse(bufferStream);
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
}

/// <summary>POST <c>/api/edge/replay</c> request shape.</summary>
public sealed record EdgeReplayRequestDto(
    string EdgeNodeId,
    IReadOnlyList<EdgeReplayEventDto> Events);

/// <summary>One event in the request batch.</summary>
public sealed record EdgeReplayEventDto(
    string EventTypeHint,
    long TenantId,
    DateTimeOffset EdgeTimestamp,
    JsonElement Payload);

/// <summary>POST <c>/api/edge/replay</c> response shape.</summary>
public sealed record EdgeReplayResponseDto(
    IReadOnlyList<EdgeReplayResultDto> Results);

/// <summary>Per-entry result.</summary>
public sealed record EdgeReplayResultDto(
    bool Ok,
    string? Error);

/// <summary>
/// Internal payload-shape parser. Mirrors the load-bearing fields of
/// <see cref="NickERP.Platform.Audit.Events.DomainEvent"/> the server
/// needs to write the audit row. Extra fields on the payload are
/// preserved by the augment-and-store path (the original is re-
/// emitted with replay metadata appended).
/// </summary>
public sealed record AuditEventReplayPayload(
    string? EventType,
    string? EntityType,
    string? EntityId,
    Guid? ActorUserId,
    string? CorrelationId);
