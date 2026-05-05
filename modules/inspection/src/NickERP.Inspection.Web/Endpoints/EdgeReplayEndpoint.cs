using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Edge.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Endpoints;

/// <summary>
/// Sprint 11 / P2 + Sprint 17 / P2-FU-multi-event-types — server-side
/// replay endpoint that accepts buffered events from edge nodes and
/// writes them into <c>audit.events</c> as fresh appends.
///
/// <para>
/// <b>Sprint 17 — three event types now supported.</b>
/// <list type="bullet">
///   <item><description><see cref="AuditEventReplayHint"/> (<c>"audit.event.replay"</c>) — the original Sprint 11 path. Payload is a <c>DomainEvent</c>-shape; the server writes it verbatim to <c>audit.events</c> with the eventType/entityType/entityId taken from the payload.</description></item>
///   <item><description><see cref="ScanCapturedHint"/> (<c>"inspection.scan.captured"</c>) — Sprint 17. Payload describes a raw scanner artifact: scannerId + locationId + sourcePath + subjectIdentifier. The server writes an audit row with <c>EventType = "inspection.scan.captured"</c>, <c>EntityType = "ScanArtifact"</c>, <c>EntityId = sourcePath</c>. The actual <c>CaseWorkflowService.IngestRawArtifactAsync</c> wiring is intentionally deferred — the audit row is the durable record; an inspection-side projector consumes them in a future sprint.</description></item>
///   <item><description><see cref="ScannerStatusChangedHint"/> (<c>"inspection.scanner.status.changed"</c>) — Sprint 17. Payload describes a scanner device's reported status. The server writes an audit row with <c>EventType = "inspection.scanner.status.changed"</c>, <c>EntityType = "ScannerDeviceInstance"</c>, <c>EntityId = scannerId</c>; downstream "latest status" projection lands in a future sprint.</description></item>
/// </list>
/// In all three cases the edge-captured <c>edgeTimestamp</c> becomes
/// <c>OccurredAt</c>; the server's wall-clock at replay-time becomes
/// <c>IngestedAt</c>; the audit row's <c>Payload</c> jsonb is augmented
/// with replay metadata (<c>replay_source</c>, <c>replay_node_id</c>,
/// <c>replayed_at</c>). The deterministic idempotency key + the
/// <c>(TenantId, IdempotencyKey)</c> unique index keep duplicate
/// replays collapsing to a single audit row.
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

    /// <summary>Hint string for the original Sprint 11 audit-event-replay path. Payload is a <c>DomainEvent</c>-shape; the server writes it verbatim to <c>audit.events</c>.</summary>
    public const string AuditEventReplayHint = "audit.event.replay";

    /// <summary>Sprint 17 — hint string for raw scanner artifacts captured at the edge. Payload carries scanner+location+sourcePath+subjectIdentifier; server writes an audit row with <c>EventType = "inspection.scan.captured"</c>.</summary>
    public const string ScanCapturedHint = "inspection.scan.captured";

    /// <summary>Sprint 17 — hint string for scanner device status changes captured at the edge. Payload carries scannerId+locationId+status; server writes an audit row with <c>EventType = "inspection.scanner.status.changed"</c>.</summary>
    public const string ScannerStatusChangedHint = "inspection.scanner.status.changed";

    /// <summary>
    /// Sprint 45 / Phase B — hint string for canonical scan-package
    /// replays. Payload is a serialised <see cref="ScanPackage"/>
    /// envelope (per <c>NickERP.Inspection.Edge.Abstractions</c>). The
    /// endpoint validates the manifest sha256 + HMAC signature under
    /// the per-edge key (Sprint 13 EdgeAuthHandler reads same key) and,
    /// on success, persists ScanArtifact rows with the manifest fields
    /// stored alongside.
    /// </summary>
    public const string ScanPackageHint = "inspection.scan.package";

    /// <summary>Sprint 45 / Phase B — audit event emitted when manifest validation succeeds.</summary>
    public const string ManifestValidatedAudit = "nickerp.edge.replay.manifest_validated";

    /// <summary>Sprint 45 / Phase B — audit event emitted when manifest validation fails.</summary>
    public const string ManifestValidationFailedAudit = "nickerp.edge.replay.manifest_validation_failed";

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
        IServiceProvider? services = null,
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
            EdgeReplayResultDto entryResult;
            // Sprint 45 / Phase B — scan-package hint takes a different
            // path: validate manifest sha256 + HMAC signature under the
            // per-edge key, persist ScanArtifact rows on success, audit
            // either way. The audit-event-replay paths (Sprint 11 / 17)
            // stay on ProcessOneAsync.
            if (string.Equals(evt.EventTypeHint, ScanPackageHint, StringComparison.Ordinal))
            {
                if (services is null)
                {
                    // Tests without a full service-provider can't exercise
                    // the scan-package path (it needs InspectionDbContext +
                    // AuditDbContext via DI). Fail-fast with a per-entry
                    // error so the lack of wiring is loud, not silent.
                    entryResult = new EdgeReplayResultDto(false,
                        "scan-package replay requires the full DI provider; configure HandleAsync(services:...) in the host wiring");
                }
                else
                {
                    entryResult = await ProcessScanPackageAsync(
                        services, body.EdgeNodeId, evt, authorizedSet,
                        authResult.PresentedKey, now, logger, ct);
                }
            }
            else
            {
                entryResult = await ProcessOneAsync(
                    db, body.EdgeNodeId, evt, authorizedSet, now, logger, ct);
            }
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
    ///
    /// <para>
    /// **Sprint 17 — per-hint dispatch.** The handler is shaped as
    /// "common pre-checks (auth + future-timestamp) → per-hint
    /// payload-shape resolution → common audit-row write". Each
    /// supported <c>EventTypeHint</c> resolves to a different fixed
    /// metadata triple (<c>EventType</c>/<c>EntityType</c>/<c>EntityId</c>);
    /// for <see cref="AuditEventReplayHint"/> the values come FROM
    /// the payload, for <see cref="ScanCapturedHint"/> and
    /// <see cref="ScannerStatusChangedHint"/> the <c>EventType</c> is
    /// fixed and the <c>EntityType</c>/<c>EntityId</c> come from a
    /// strictly typed payload-resolver. Adding a new hint = adding a
    /// case to <see cref="ResolveAuditMetadata"/> and (maybe) a new
    /// payload-shape record below.
    /// </para>
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
        // ---- 3a. Authorization. --------------------------------------
        if (!authorizedTenants.Contains(evt.TenantId))
        {
            return new EdgeReplayResultDto(false,
                $"tenant {evt.TenantId} not authorized for edge {edgeNodeId}");
        }

        // ---- 3b. Reject future-dated edge timestamps. ----------------
        // The spec carves these out: "Post events with future
        // timestamps (server rejects)." A small clock-skew tolerance
        // (60s) absorbs jittery NTP without admitting wildly wrong
        // values.
        if (evt.EdgeTimestamp > replayedAt.AddSeconds(60))
        {
            return new EdgeReplayResultDto(false,
                $"edge timestamp {evt.EdgeTimestamp:O} is more than 60s in the future relative to server time {replayedAt:O}");
        }

        // ---- 3c. Per-hint dispatch — resolve the audit-row metadata.
        // Each supported hint maps the payload to (EventType,
        // EntityType, EntityId, ActorUserId, CorrelationId). An
        // unsupported hint returns a per-entry permanent error.
        ResolvedAuditMetadata? meta;
        try
        {
            meta = ResolveAuditMetadata(evt.EventTypeHint, evt.Payload);
        }
        catch (JsonException ex)
        {
            return new EdgeReplayResultDto(false, $"invalid payload JSON: {ex.Message}");
        }
        if (meta is null)
        {
            return new EdgeReplayResultDto(false,
                $"unsupported eventTypeHint '{evt.EventTypeHint}'. " +
                $"Supported: '{AuditEventReplayHint}', '{ScanCapturedHint}', '{ScannerStatusChangedHint}'.");
        }
        if (meta.Value.Error is not null)
        {
            return new EdgeReplayResultDto(false, meta.Value.Error);
        }

        // ---- 3d. Build deterministic idempotency key. ----------------
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

        // ---- 3e. Build the audit-row payload (with replay metadata). -
        // We re-serialise the original payload with three extra fields
        // injected at the top level: replay_source, replay_node_id,
        // replayed_at. Consumers can spot edge-replayed events by the
        // presence of replay_source.
        using var augmentedJson = AugmentPayload(evt.Payload, edgeNodeId, replayedAt);

        var row = new DomainEventRow
        {
            EventId = Guid.NewGuid(),
            TenantId = evt.TenantId,
            ActorUserId = meta.Value.ActorUserId,
            CorrelationId = meta.Value.CorrelationId,
            EventType = meta.Value.EventType,
            EntityType = meta.Value.EntityType,
            EntityId = meta.Value.EntityId,
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
    /// Sprint 45 / Phase B — scan-package validation + persistence.
    ///
    /// <para>
    /// <b>Flow.</b>
    /// <list type="number">
    ///   <item><description>Authorization check — same per-edge / per-tenant gating as Sprint 11 / 17 paths.</description></item>
    ///   <item><description>Future-timestamp guard — same 60s tolerance.</description></item>
    ///   <item><description>Plaintext key required — the per-edge HMAC signing key is the presented X-Edge-Api-Key plaintext (Sprint 13 contract; no new key infrastructure). Legacy X-Edge-Token path cannot sign manifests.</description></item>
    ///   <item><description>Wire-DTO → canonical <see cref="ScanPackage"/> conversion. Base64 image data decoded; manifest sha256 + signature decoded.</description></item>
    ///   <item><description><see cref="ScanPackageValidator.Validate"/> runs the three-stage check (required-field shape, per-image sha256, manifest sha256 + HMAC signature).</description></item>
    ///   <item><description>On failure: emit <see cref="ManifestValidationFailedAudit"/>; return 400-shaped per-entry error indicating which check failed.</description></item>
    ///   <item><description>On success: persist one <see cref="ScanArtifact"/> per <see cref="ImageFile"/> with manifest fields stored alongside; emit <see cref="ManifestValidatedAudit"/>; return ok.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    private static async Task<EdgeReplayResultDto> ProcessScanPackageAsync(
        IServiceProvider services,
        string edgeNodeId,
        EdgeReplayEventDto evt,
        HashSet<long> authorizedTenants,
        string? presentedKey,
        DateTimeOffset replayedAt,
        ILogger logger,
        CancellationToken ct)
    {
        // ---- Authorization (per-edge / per-tenant). ---------------------
        if (!authorizedTenants.Contains(evt.TenantId))
        {
            return new EdgeReplayResultDto(false,
                $"tenant {evt.TenantId} not authorized for edge {edgeNodeId}");
        }

        // ---- Future-timestamp guard. ------------------------------------
        if (evt.EdgeTimestamp > replayedAt.AddSeconds(60))
        {
            return new EdgeReplayResultDto(false,
                $"edge timestamp {evt.EdgeTimestamp:O} is more than 60s in the future relative to server time {replayedAt:O}");
        }

        // ---- Plaintext key required. ------------------------------------
        // The legacy X-Edge-Token path cannot sign manifests — there's
        // only one shared secret and it's not a per-edge HMAC key. Refuse
        // scan-package replay on the legacy path so an attacker who guessed
        // the legacy token can't replay scans on behalf of any edge.
        if (string.IsNullOrEmpty(presentedKey))
        {
            return new EdgeReplayResultDto(false,
                "scan-package replay requires a per-edge X-Edge-Api-Key header (legacy X-Edge-Token cannot sign manifests)");
        }

        // ---- Wire-DTO → ScanPackage. ------------------------------------
        ScanPackage package;
        try
        {
            var dto = evt.Payload.Deserialize<ScanPackageWireDto>(ScanPackageJsonOptions);
            if (dto is null)
            {
                return await EmitManifestFailureAsync(services, edgeNodeId, evt, replayedAt,
                    ScanPackageFailureKind.RequiredFieldMissing,
                    "scan-package payload could not be deserialised", logger, ct);
            }
            package = dto.ToScanPackage();
        }
        catch (FormatException ex)
        {
            return await EmitManifestFailureAsync(services, edgeNodeId, evt, replayedAt,
                ScanPackageFailureKind.RequiredFieldMissing,
                $"scan-package payload base64 decode failed: {ex.Message}", logger, ct);
        }
        catch (JsonException ex)
        {
            return await EmitManifestFailureAsync(services, edgeNodeId, evt, replayedAt,
                ScanPackageFailureKind.RequiredFieldMissing,
                $"scan-package payload JSON malformed: {ex.Message}", logger, ct);
        }

        // ---- Manifest validation. ---------------------------------------
        var hmacKey = Encoding.UTF8.GetBytes(presentedKey);
        var validation = ScanPackageValidator.Validate(package, hmacKey);
        if (!validation.IsValid)
        {
            return await EmitManifestFailureAsync(services, edgeNodeId, evt, replayedAt,
                validation.FailureKind ?? ScanPackageFailureKind.RequiredFieldMissing,
                validation.FailureReason ?? "manifest validation failed",
                logger, ct);
        }

        // ---- Persistence + success audit. --------------------------------
        return await PersistScanPackageAsync(
            services, edgeNodeId, evt, package, validation.ManifestJson!, replayedAt, logger, ct);
    }

    /// <summary>
    /// Sprint 45 / Phase B — emit a <see cref="ManifestValidationFailedAudit"/>
    /// audit event + return a per-entry failure result. The audit row
    /// is best-effort: if the audit DB is unavailable we still return
    /// the failure to the caller (the per-entry response carries the
    /// load-bearing diagnostic).
    /// </summary>
    private static async Task<EdgeReplayResultDto> EmitManifestFailureAsync(
        IServiceProvider services,
        string edgeNodeId,
        EdgeReplayEventDto evt,
        DateTimeOffset replayedAt,
        ScanPackageFailureKind kind,
        string reason,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var auditDb = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var tenantCtx = scope.ServiceProvider.GetRequiredService<ITenantContext>();
            tenantCtx.SetSystemContext();

            var scanId = TryReadScanId(evt.Payload);
            var payload = JsonSerializer.SerializeToDocument(new
            {
                edgeNodeId,
                tenantId = evt.TenantId,
                scanId,
                failureReason = reason,
                failureKind = kind.ToString()
            });
            var idempotencyKey = ComputeManifestAuditKey(
                edgeNodeId, evt.TenantId, evt.EdgeTimestamp, scanId, kind);
            auditDb.Events.Add(new DomainEventRow
            {
                EventId = Guid.NewGuid(),
                TenantId = evt.TenantId,
                ActorUserId = null,
                CorrelationId = null,
                EventType = ManifestValidationFailedAudit,
                EntityType = "ScanPackage",
                EntityId = scanId ?? string.Empty,
                Payload = payload,
                OccurredAt = evt.EdgeTimestamp,
                IngestedAt = replayedAt,
                IdempotencyKey = idempotencyKey,
                PrevEventHash = null
            });
            await auditDb.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "EdgeReplayEndpoint: failed to write manifest_validation_failed audit row for edge={Edge} tenant={Tenant}.",
                edgeNodeId, evt.TenantId);
        }

        return new EdgeReplayResultDto(
            false,
            $"manifest validation failed ({kind}): {reason}");
    }

    /// <summary>
    /// Sprint 45 / Phase B — persist one <see cref="ScanArtifact"/> per
    /// <see cref="ImageFile"/> on the inspection DB, with manifest
    /// fields stored alongside (<see cref="ScanArtifact.ManifestJson"/>,
    /// <see cref="ScanArtifact.ManifestSha256"/>,
    /// <see cref="ScanArtifact.ManifestSignature"/>,
    /// <see cref="ScanArtifact.ManifestVerifiedAt"/>). Then emit
    /// <see cref="ManifestValidatedAudit"/>.
    ///
    /// <para>
    /// The artifact rows are NOT linked to a Scan/Case yet — the
    /// inspection-side workflow projector consumes the manifest_validated
    /// audit events in a future sprint and binds them to cases. The
    /// rows here are durable, content-addressed (per-file Sha256Hex →
    /// <see cref="ScanArtifact.ContentHash"/>), and idempotent
    /// (re-replay of the same package collapses on
    /// <c>(TenantId, ContentHash)</c> via the existing index posture).
    /// </para>
    /// </summary>
    private static async Task<EdgeReplayResultDto> PersistScanPackageAsync(
        IServiceProvider services,
        string edgeNodeId,
        EdgeReplayEventDto evt,
        ScanPackage package,
        byte[] manifestJson,
        DateTimeOffset replayedAt,
        ILogger logger,
        CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var inspectionDb = scope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var auditDb = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var tenantCtx = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantCtx.SetTenant(evt.TenantId);

        // De-dup: if any of these (TenantId, ContentHash) rows already
        // exist, the replay collapses to a no-op success — the
        // canonical bundle is content-addressed.
        var existingHashes = new HashSet<string>(StringComparer.Ordinal);
        if (package.ImageFiles is { Count: > 0 })
        {
            var hashes = package.ImageFiles.Select(f => f.Sha256Hex).ToList();
            var existing = await inspectionDb.ScanArtifacts
                .Where(a => a.TenantId == evt.TenantId && hashes.Contains(a.ContentHash))
                .Select(a => a.ContentHash)
                .ToListAsync(ct);
            foreach (var h in existing) existingHashes.Add(h);
        }

        var manifestSha256 = package.ManifestSha256.ToArray();
        var manifestSignature = package.ManifestSignature.ToArray();
        var manifestJsonString = Encoding.UTF8.GetString(manifestJson);
        var nowUtc = replayedAt;

        var newRows = 0;
        foreach (var file in package.ImageFiles ?? Array.Empty<ImageFile>())
        {
            if (existingHashes.Contains(file.Sha256Hex)) continue;

            inspectionDb.ScanArtifacts.Add(new ScanArtifact
            {
                Id = Guid.NewGuid(),
                ScanId = Guid.Empty, // unbound; workflow projector binds later
                ArtifactKind = MapView(file.View),
                StorageUri = $"edge-replay://{edgeNodeId}/{package.ScanId}/{file.FileName}",
                MimeType = "application/octet-stream",
                WidthPx = 0,
                HeightPx = 0,
                Channels = 1,
                ContentHash = file.Sha256Hex,
                MetadataJson = BuildArtifactMetadataJson(package, file),
                CreatedAt = replayedAt,
                ManifestJson = manifestJsonString,
                ManifestSha256 = manifestSha256,
                ManifestSignature = manifestSignature,
                ManifestVerifiedAt = nowUtc,
                TenantId = evt.TenantId
            });
            newRows++;
        }

        if (newRows > 0)
        {
            try
            {
                await inspectionDb.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogError(ex,
                    "EdgeReplayEndpoint: ScanArtifact persistence failed for edge={Edge} tenant={Tenant} scanId={ScanId}.",
                    edgeNodeId, evt.TenantId, package.ScanId);
                return new EdgeReplayResultDto(false, $"persistence failure: {ex.Message}");
            }
        }

        // Success audit on the audit DB.
        try
        {
            tenantCtx.SetSystemContext();
            var payload = JsonSerializer.SerializeToDocument(new
            {
                edgeNodeId,
                tenantId = evt.TenantId,
                scanId = package.ScanId,
                imageCount = package.ImageFiles?.Count ?? 0,
                newArtifactRows = newRows
            });
            var idempotencyKey = ComputeManifestAuditKey(
                edgeNodeId, evt.TenantId, evt.EdgeTimestamp, package.ScanId, kind: null);
            auditDb.Events.Add(new DomainEventRow
            {
                EventId = Guid.NewGuid(),
                TenantId = evt.TenantId,
                ActorUserId = null,
                CorrelationId = null,
                EventType = ManifestValidatedAudit,
                EntityType = "ScanPackage",
                EntityId = package.ScanId,
                Payload = payload,
                OccurredAt = evt.EdgeTimestamp,
                IngestedAt = replayedAt,
                IdempotencyKey = idempotencyKey,
                PrevEventHash = null
            });
            await auditDb.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "EdgeReplayEndpoint: failed to write manifest_validated audit row for edge={Edge} tenant={Tenant} scanId={ScanId}.",
                edgeNodeId, evt.TenantId, package.ScanId);
        }

        return new EdgeReplayResultDto(true, null);
    }

    /// <summary>
    /// Sprint 45 / Phase B — best-effort read of <c>scan_id</c> from the
    /// raw payload, used to populate audit-row <c>EntityId</c> when
    /// validation fails before we can deserialise the full package.
    /// </summary>
    private static string? TryReadScanId(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (payload.TryGetProperty("scan_id", out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        if (payload.TryGetProperty("scanId", out var camel) && camel.ValueKind == JsonValueKind.String)
            return camel.GetString();
        return null;
    }

    /// <summary>
    /// Sprint 45 / Phase B — deterministic idempotency key for manifest
    /// audit rows. Distinct namespace from the Sprint 11 / 17 path so a
    /// scan-package replay doesn't collide with an audit-event replay
    /// from the same edge at the same instant.
    /// </summary>
    private static string ComputeManifestAuditKey(
        string edgeNodeId, long tenantId, DateTimeOffset edgeTimestamp,
        string? scanId, ScanPackageFailureKind? kind)
    {
        var marker = kind?.ToString() ?? "ok";
        var idPart = scanId ?? edgeTimestamp.UtcTicks.ToString();
        return $"manifest|{edgeNodeId}|{tenantId}|{idPart}|{marker}";
    }

    /// <summary>
    /// Sprint 45 / Phase B — map vendor-neutral view tag to the
    /// <see cref="ScanArtifact.ArtifactKind"/> column. Free-form on
    /// either side; this just normalises common values so the dashboard
    /// can group sensibly.
    /// </summary>
    private static string MapView(string? view)
    {
        if (string.IsNullOrWhiteSpace(view)) return "Primary";
        return view.ToLowerInvariant() switch
        {
            "primary" => "Primary",
            "high-energy" => "HighEnergy",
            "low-energy" => "LowEnergy",
            "material" => "Material",
            "side" or "side-view" => "SideView",
            _ => view
        };
    }

    /// <summary>
    /// Sprint 45 / Phase B — build the per-artifact metadata JSON
    /// stored on <see cref="ScanArtifact.MetadataJson"/>. Keeps the
    /// vendor-neutral view tag, the source filename, and the package
    /// identity for downstream binding.
    /// </summary>
    private static string BuildArtifactMetadataJson(ScanPackage package, ImageFile file)
    {
        var doc = new
        {
            scan_id = package.ScanId,
            scanner_id = package.ScannerId,
            site_id = package.SiteId,
            gateway_id = package.GatewayId,
            scan_type = package.ScanType,
            view = file.View ?? string.Empty,
            file_name = file.FileName,
            size_bytes = file.SizeBytes,
            occurred_at = package.OccurredAt.ToString("O"),
            container_number = package.ContainerNumber ?? string.Empty,
            vehicle_plate = package.VehiclePlate ?? string.Empty,
            declaration_number = package.DeclarationNumber ?? string.Empty,
            manifest_number = package.ManifestNumber ?? string.Empty
        };
        return JsonSerializer.Serialize(doc, ScanPackageJsonOptions);
    }

    /// <summary>
    /// Sprint 45 / Phase B — JSON options for the wire-format scan
    /// package payload. Snake-case property naming matches the
    /// canonical manifest shape (see
    /// <see cref="ScanPackageManifest.BuildManifestJson"/>).
    /// </summary>
    internal static readonly JsonSerializerOptions ScanPackageJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    /// <summary>
    /// Sprint 17 — resolve the audit-row metadata for one event based on
    /// its <paramref name="eventTypeHint"/>. Returns:
    /// <list type="bullet">
    ///   <item><description><c>null</c> when the hint is unrecognised.</description></item>
    ///   <item><description>A <see cref="ResolvedAuditMetadata"/> with <c>Error</c> populated when the hint matches but the payload is malformed.</description></item>
    ///   <item><description>A <see cref="ResolvedAuditMetadata"/> with <c>Error == null</c> when the metadata is good to write.</description></item>
    /// </list>
    /// Public so unit tests can drive payload-shape edge cases without
    /// running the whole handler.
    /// </summary>
    public static ResolvedAuditMetadata? ResolveAuditMetadata(
        string eventTypeHint, JsonElement payload)
    {
        if (string.Equals(eventTypeHint, AuditEventReplayHint, StringComparison.Ordinal))
        {
            return ResolveAuditEventReplayMetadata(payload);
        }
        if (string.Equals(eventTypeHint, ScanCapturedHint, StringComparison.Ordinal))
        {
            return ResolveScanCapturedMetadata(payload);
        }
        if (string.Equals(eventTypeHint, ScannerStatusChangedHint, StringComparison.Ordinal))
        {
            return ResolveScannerStatusChangedMetadata(payload);
        }
        return null;
    }

    /// <summary>
    /// Sprint 11 path — payload IS the <c>DomainEvent</c> shape. Pull
    /// the metadata triple straight off the parsed shape.
    /// </summary>
    private static ResolvedAuditMetadata ResolveAuditEventReplayMetadata(JsonElement payload)
    {
        var parsed = payload.Deserialize<AuditEventReplayPayload>(JsonOptions);
        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.EventType)
            || string.IsNullOrWhiteSpace(parsed.EntityType)
            || string.IsNullOrWhiteSpace(parsed.EntityId))
        {
            return ResolvedAuditMetadata.Failed(
                "payload missing required fields: eventType, entityType, entityId");
        }
        return new ResolvedAuditMetadata(
            EventType: parsed.EventType!,
            EntityType: parsed.EntityType!,
            EntityId: parsed.EntityId!,
            ActorUserId: parsed.ActorUserId,
            CorrelationId: parsed.CorrelationId,
            Error: null);
    }

    /// <summary>
    /// Sprint 17 path — payload describes a raw scanner artifact. The
    /// audit row's <c>EventType</c> is fixed (<c>"inspection.scan.captured"</c>);
    /// <c>EntityType</c> = "ScanArtifact"; <c>EntityId</c> = the
    /// payload's <c>sourcePath</c> (stable across edge → server).
    /// </summary>
    private static ResolvedAuditMetadata ResolveScanCapturedMetadata(JsonElement payload)
    {
        var parsed = payload.Deserialize<ScanCapturedPayload>(JsonOptions);
        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.SourcePath)
            || string.IsNullOrWhiteSpace(parsed.ScannerId))
        {
            return ResolvedAuditMetadata.Failed(
                "scan-captured payload missing required fields: scannerId, sourcePath");
        }
        return new ResolvedAuditMetadata(
            EventType: ScanCapturedHint,
            EntityType: "ScanArtifact",
            EntityId: parsed.SourcePath!,
            ActorUserId: parsed.ActorUserId,
            CorrelationId: parsed.CorrelationId,
            Error: null);
    }

    /// <summary>
    /// Sprint 17 path — payload describes a scanner status change. The
    /// audit row's <c>EventType</c> is fixed
    /// (<c>"inspection.scanner.status.changed"</c>); <c>EntityType</c>
    /// = "ScannerDeviceInstance"; <c>EntityId</c> = the payload's
    /// <c>scannerId</c>.
    /// </summary>
    private static ResolvedAuditMetadata ResolveScannerStatusChangedMetadata(JsonElement payload)
    {
        var parsed = payload.Deserialize<ScannerStatusChangedPayload>(JsonOptions);
        if (parsed is null
            || string.IsNullOrWhiteSpace(parsed.ScannerId)
            || string.IsNullOrWhiteSpace(parsed.Status))
        {
            return ResolvedAuditMetadata.Failed(
                "scanner-status-changed payload missing required fields: scannerId, status");
        }
        return new ResolvedAuditMetadata(
            EventType: ScannerStatusChangedHint,
            EntityType: "ScannerDeviceInstance",
            EntityId: parsed.ScannerId!,
            ActorUserId: parsed.ActorUserId,
            CorrelationId: parsed.CorrelationId,
            Error: null);
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

/// <summary>
/// Sprint 17 — payload shape for <see cref="EdgeReplayEndpoint.ScanCapturedHint"/>
/// events. Carries the metadata about a raw artifact captured by a
/// scanner adapter at the edge; the file content itself stays on the
/// scanner box and is fetched out-of-band by the inspection-side
/// projector. <see cref="SourcePath"/> is required (it's the audit
/// row's <c>EntityId</c>); <see cref="ScannerId"/> is required (the
/// inspection-side consumer needs it to resolve the
/// <c>ScannerDeviceInstance</c>).
/// </summary>
public sealed record ScanCapturedPayload(
    string? ScannerId,
    string? LocationId,
    string? SourcePath,
    string? SubjectIdentifier,
    Guid? ActorUserId,
    string? CorrelationId);

/// <summary>
/// Sprint 17 — payload shape for
/// <see cref="EdgeReplayEndpoint.ScannerStatusChangedHint"/> events.
/// <see cref="ScannerId"/> + <see cref="Status"/> are required;
/// <see cref="StatusDetail"/> is free-form (e.g. error code, idle
/// reason).
/// </summary>
public sealed record ScannerStatusChangedPayload(
    string? ScannerId,
    string? Status,
    string? StatusDetail,
    Guid? ActorUserId,
    string? CorrelationId);

/// <summary>
/// Sprint 45 / Phase B — wire-format DTO for a canonical
/// <see cref="ScanPackage"/> payload received via
/// <c>/api/edge/replay</c>. Snake-case property names match the
/// canonical manifest JSON shape (see
/// <see cref="ScanPackageManifest.BuildManifestJson"/>); image bytes
/// arrive as base64 strings.
///
/// <para>
/// <b>Why a wire DTO.</b> The in-memory <see cref="ScanPackage"/>
/// record holds <see cref="ImageFile.Data"/> as raw bytes; the JSON
/// envelope encodes them as base64 strings. The DTO bridges the two —
/// validation runs against the in-memory shape, the wire shape stays
/// transport-friendly.
/// </para>
/// </summary>
public sealed class ScanPackageWireDto
{
    public string ScanId { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string ScannerId { get; set; } = string.Empty;
    public string GatewayId { get; set; } = string.Empty;
    public string ScanType { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public string OperatorId { get; set; } = string.Empty;
    public string ContainerNumber { get; set; } = string.Empty;
    public string VehiclePlate { get; set; } = string.Empty;
    public string DeclarationNumber { get; set; } = string.Empty;
    public string ManifestNumber { get; set; } = string.Empty;
    public List<ImageFileWireDto> ImageFiles { get; set; } = new();
    /// <summary>Base64 of the manifest sha256 digest.</summary>
    public string ManifestSha256 { get; set; } = string.Empty;
    /// <summary>Base64 of the manifest HMAC-SHA256 signature.</summary>
    public string ManifestSignature { get; set; } = string.Empty;

    /// <summary>
    /// Convert the wire DTO into an in-memory
    /// <see cref="ScanPackage"/>. May throw <see cref="FormatException"/>
    /// for malformed base64 — the endpoint catches and emits a
    /// per-entry RequiredFieldMissing failure.
    /// </summary>
    public ScanPackage ToScanPackage()
    {
        var images = (ImageFiles ?? new List<ImageFileWireDto>())
            .Select(f => f.ToImageFile())
            .ToList();
        return new ScanPackage(
            ScanId: ScanId ?? string.Empty,
            SiteId: SiteId ?? string.Empty,
            ScannerId: ScannerId ?? string.Empty,
            GatewayId: GatewayId ?? string.Empty,
            ScanType: ScanType ?? string.Empty,
            OccurredAt: OccurredAt,
            OperatorId: OperatorId ?? string.Empty,
            ContainerNumber: ContainerNumber ?? string.Empty,
            VehiclePlate: VehiclePlate ?? string.Empty,
            DeclarationNumber: DeclarationNumber ?? string.Empty,
            ManifestNumber: ManifestNumber ?? string.Empty,
            ImageFiles: images,
            ManifestSha256: string.IsNullOrEmpty(ManifestSha256)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(ManifestSha256),
            ManifestSignature: string.IsNullOrEmpty(ManifestSignature)
                ? Array.Empty<byte>()
                : Convert.FromBase64String(ManifestSignature));
    }
}

/// <summary>
/// Sprint 45 / Phase B — wire-format DTO for one image inside a
/// <see cref="ScanPackageWireDto"/>. <see cref="Data"/> is base64.
/// </summary>
public sealed class ImageFileWireDto
{
    public string FileName { get; set; } = string.Empty;
    public string Sha256Hex { get; set; } = string.Empty;
    public string View { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    /// <summary>Base64 of the raw image bytes.</summary>
    public string Data { get; set; } = string.Empty;

    public ImageFile ToImageFile()
    {
        var bytes = string.IsNullOrEmpty(Data)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(Data);
        return new ImageFile(
            FileName: FileName ?? string.Empty,
            Sha256Hex: Sha256Hex ?? string.Empty,
            View: View ?? string.Empty,
            SizeBytes: SizeBytes,
            Data: bytes);
    }
}

/// <summary>
/// Sprint 17 — resolved <see cref="DomainEventRow"/> metadata for one
/// event in a replay batch. Returned by
/// <see cref="EdgeReplayEndpoint.ResolveAuditMetadata"/>; the handler
/// uses it to build the audit row without re-checking the hint.
///
/// <para>
/// When <see cref="Error"/> is non-null the rest of the fields are
/// undefined and the caller emits a per-entry failure.
/// </para>
/// </summary>
public readonly record struct ResolvedAuditMetadata(
    string EventType,
    string EntityType,
    string EntityId,
    Guid? ActorUserId,
    string? CorrelationId,
    string? Error)
{
    /// <summary>Build a failure-shaped metadata holder. The caller checks <see cref="Error"/> and emits a per-entry failure.</summary>
    public static ResolvedAuditMetadata Failed(string error) =>
        new(string.Empty, string.Empty, string.Empty, null, null, error);
}
