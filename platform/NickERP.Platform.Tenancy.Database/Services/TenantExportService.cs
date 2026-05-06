using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy.Database.Storage;
using NickERP.Platform.Tenancy.Database.Workers;
using NickERP.Platform.Tenancy.Entities;
using Npgsql;

namespace NickERP.Platform.Tenancy.Database.Services;

/// <summary>
/// Sprint 25 — Tenant lifecycle Pt 3. Closes the
/// <c>FU-tenant-lifecycle</c> followup with platform-admin-generated
/// scoped exports of a tenant's data. Each request is audit-trailed
/// through Pending / Running / Completed / Failed / Expired / Revoked.
/// Sprint 51 / Phase B added the LISTEN/NOTIFY pickup channel
/// (<see cref="TenantExportRunner.NotifyChannel"/>) so the runner
/// dispatches new requests within a second instead of waiting for the
/// 30 s poll.
/// </summary>
/// <remarks>
/// <para>
/// Cross-DB orchestration mirrors <see cref="TenantPurgeOrchestrator"/>:
/// sequential per-DB reads, NOT one giant transaction. The runner sets
/// <c>app.tenant_id = '&lt;exportTarget&gt;'</c> per connection so the
/// existing per-table RLS USING clauses admit reads of that tenant's
/// rows. No table opt-in is required — same posture as the purge
/// orchestrator.
/// </para>
/// <para>
/// Storage: filesystem, configured via <c>Tenancy:Export:OutputPath</c>
/// (default <c>var/tenant-exports/{tenantId}/{exportId}.zip</c>). S3 /
/// blob-store is a follow-up if the user asks. Auto-expire defaults to
/// 7 days, configurable via <c>Tenancy:Export:RetentionDays</c>.
/// </para>
/// <para>
/// Security: every download path verifies (a) the row hasn't been
/// revoked, (b) the row hasn't expired, (c) the requesting user matches
/// the requesting-user-id captured at request time OR is a platform
/// admin (callers higher in the stack do the role check; this service
/// gets the user id and trusts the role gate). Every download bumps
/// <see cref="TenantExportRequest.DownloadCount"/> and emits a
/// <c>nickerp.tenancy.tenant_export_downloaded</c> audit event.
/// </para>
/// </remarks>
public interface ITenantExportService
{
    /// <summary>
    /// Create a Pending row in <c>tenant_export_requests</c>. The
    /// <see cref="TenantExportRunner"/> background service picks it up
    /// shortly. Emits a <c>nickerp.tenancy.tenant_export_requested</c>
    /// audit event.
    /// </summary>
    Task<TenantExportRequest> RequestExportAsync(
        long tenantId,
        TenantExportFormat format,
        TenantExportScope scope,
        Guid requestingUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Look up the current status of a single export request.
    /// </summary>
    Task<TenantExportRequest?> GetExportStatusAsync(Guid exportId, CancellationToken ct = default);

    /// <summary>
    /// Stream the artifact bytes if the export is in
    /// <see cref="TenantExportStatus.Completed"/> and not expired /
    /// revoked. Bumps <see cref="TenantExportRequest.DownloadCount"/>
    /// and emits a <c>tenant_export_downloaded</c> audit event.
    /// Returns null if the export is not downloadable; the caller
    /// should map that to 404.
    /// </summary>
    Task<TenantExportDownload?> DownloadExportAsync(
        Guid exportId,
        Guid requestingUserId,
        CancellationToken ct = default);

    /// <summary>
    /// Revoke an export — flips the status to
    /// <see cref="TenantExportStatus.Revoked"/> and deletes the
    /// artifact on disk if present. Emits
    /// <c>tenant_export_revoked</c>.
    /// </summary>
    Task RevokeExportAsync(Guid exportId, Guid revokingUserId, CancellationToken ct = default);

    /// <summary>
    /// List recent exports for a tenant, newest first. Used by the
    /// admin UI's per-tenant Exports card.
    /// </summary>
    Task<IReadOnlyList<TenantExportRequest>> ListExportsAsync(long tenantId, CancellationToken ct = default);
}

/// <summary>
/// Outcome record returned by
/// <see cref="ITenantExportService.DownloadExportAsync"/>.
/// </summary>
/// <param name="Stream">Open file stream. Caller disposes.</param>
/// <param name="ContentType">MIME type — always <c>application/zip</c>
/// today; included for forward-compat.</param>
/// <param name="FileName">Suggested file name for the
/// <c>Content-Disposition</c> header.</param>
/// <param name="SizeBytes">Bundle size in bytes.</param>
/// <param name="Sha256Hex">Hex-encoded sha256, optional integrity
/// surface for the caller's response headers.</param>
public sealed record TenantExportDownload(
    Stream Stream,
    string ContentType,
    string FileName,
    long SizeBytes,
    string Sha256Hex);

/// <summary>
/// Sprint 51 / Phase E — pickable storage backend for tenant export
/// bundles. Filesystem preserves the pre-Phase-E layout; S3 is the
/// new option.
/// </summary>
public enum TenantExportStorageBackend
{
    /// <summary>Default. Bundles written to local filesystem under
    /// <see cref="TenantExportOptions.OutputPath"/>.</summary>
    Filesystem = 0,

    /// <summary>S3-compatible blob storage. Configured via
    /// <see cref="TenantExportOptions.S3"/>. Works with AWS S3, Minio,
    /// Backblaze B2, Wasabi.</summary>
    S3 = 10
}

/// <summary>
/// Configuration for <see cref="TenantExportService"/> +
/// <see cref="TenantExportRunner"/>. Bound from
/// <c>Tenancy:Export</c>.
/// </summary>
public sealed class TenantExportOptions
{
    /// <summary>Filesystem root for export bundles. Default
    /// <c>var/tenant-exports</c>; per-tenant subdirectory created
    /// on demand.</summary>
    public string OutputPath { get; set; } = "var/tenant-exports";

    /// <summary>Sprint 51 / Phase E — which storage backend the
    /// runner persists bundles into. Default
    /// <see cref="TenantExportStorageBackend.Filesystem"/> preserves
    /// the pre-Phase-E behaviour.</summary>
    public TenantExportStorageBackend StorageBackend { get; set; } = TenantExportStorageBackend.Filesystem;

    /// <summary>Sprint 51 / Phase E — S3 configuration when
    /// <see cref="StorageBackend"/> is <see cref="TenantExportStorageBackend.S3"/>.
    /// Bound from <c>Tenancy:Export:Storage:S3</c>.</summary>
    public Storage.S3StorageOptions? S3 { get; set; }

    /// <summary>How long completed exports stay downloadable. Default 7
    /// days. After this the sweeper deletes the file and flips status
    /// to <see cref="TenantExportStatus.Expired"/>.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Connection string for <c>nickerp_inspection</c>. Null = skip
    /// inspection block in the bundle.</summary>
    public string? InspectionConnectionString { get; set; }

    /// <summary>Connection string for <c>nickerp_nickfinance</c>. Null = skip
    /// nickfinance block in the bundle.</summary>
    public string? NickFinanceConnectionString { get; set; }

    /// <summary>Connection string for <c>nickerp_platform</c>. Used for
    /// reading audit + identity rows during export. Falls back to the
    /// TenancyDbContext connection.</summary>
    public string? PlatformConnectionString { get; set; }

    /// <summary>How many concurrent exports the runner will execute at
    /// once. Default 2 — enough to keep operators productive without
    /// hammering the platform DBs.</summary>
    public int MaxConcurrentExports { get; set; } = 2;

    /// <summary>How often the runner polls for Pending rows. Default
    /// 30 s; LISTEN/NOTIFY is a future enhancement.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Override the inspection-tables list (test fixtures).
    /// Same shape as <see cref="TenantPurgeOrchestrator.InspectionTablesDefault"/>.</summary>
    public IReadOnlyList<string>? InspectionTables { get; set; }

    /// <summary>Override the nickfinance-tables list (test fixtures).</summary>
    public IReadOnlyList<string>? NickFinanceTables { get; set; }

    /// <summary>Override the audit-tables list (test fixtures).</summary>
    public IReadOnlyList<string>? AuditTables { get; set; }

    /// <summary>Override the identity-tables list (test fixtures).</summary>
    public IReadOnlyList<string>? IdentityTables { get; set; }
}

/// <summary>
/// Default <see cref="ITenantExportService"/>. Reads the
/// <see cref="TenancyDbContext"/> for request rows; the actual export
/// work runs in <see cref="TenantExportRunner"/>.
/// </summary>
public sealed class TenantExportService : ITenantExportService
{
    private readonly TenancyDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly TenantExportOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TenantExportService> _logger;
    private readonly ITenantExportStorage? _storage;

    public TenantExportService(
        TenancyDbContext db,
        IEventPublisher publisher,
        TenantExportOptions options,
        ILogger<TenantExportService> logger,
        TimeProvider? clock = null,
        ITenantExportStorage? storage = null)
    {
        _db = db;
        _publisher = publisher;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _storage = storage;
    }

    /// <inheritdoc />
    public async Task<TenantExportRequest> RequestExportAsync(
        long tenantId,
        TenantExportFormat format,
        TenantExportScope scope,
        Guid requestingUserId,
        CancellationToken ct = default)
    {
        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Tenant id must be positive.");
        }
        if (requestingUserId == Guid.Empty)
        {
            throw new ArgumentException("RequestingUserId is required.", nameof(requestingUserId));
        }

        // Verify the tenant exists. We use IgnoreQueryFilters so admins
        // can request an export of a soft-deleted tenant — the whole
        // point of the retention window is to keep the data accessible
        // for export until hard-purge.
        var tenant = await _db.Tenants
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            throw new InvalidOperationException($"Tenant {tenantId} not found.");
        }

        var now = _clock.GetUtcNow();
        var entity = new TenantExportRequest
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            RequestedAt = now,
            RequestedByUserId = requestingUserId,
            Format = format,
            Scope = scope,
            Status = TenantExportStatus.Pending,
        };
        _db.TenantExportRequests.Add(entity);
        await _db.SaveChangesAsync(ct);

        // Sprint 51 / Phase B — emit a Postgres NOTIFY so the runner
        // can dispatch within a second instead of waiting on the next
        // 30 s poll. NOTIFY is buffered until tx commit; here we're
        // outside an explicit tx (SaveChanges committed), so the
        // notification fires immediately. Best-effort: the 30 s poll
        // remains as the fallback if the channel is missed (listener
        // restart, network blip, in-memory provider in tests).
        await TryNotifyRequestedAsync(entity.Id, ct);

        await EmitAsync(tenant, "nickerp.tenancy.tenant_export_requested",
            JsonSerializer.SerializeToElement(new
            {
                exportId = entity.Id,
                tenantId,
                tenant.Code,
                format = format.ToString(),
                scope = scope.ToString(),
            }),
            requestingUserId, ct);

        return entity;
    }

    /// <inheritdoc />
    public async Task<TenantExportRequest?> GetExportStatusAsync(Guid exportId, CancellationToken ct = default)
    {
        return await _db.TenantExportRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == exportId, ct);
    }

    /// <inheritdoc />
    public async Task<TenantExportDownload?> DownloadExportAsync(
        Guid exportId,
        Guid requestingUserId,
        CancellationToken ct = default)
    {
        var row = await _db.TenantExportRequests
            .FirstOrDefaultAsync(r => r.Id == exportId, ct);
        if (row is null)
        {
            return null;
        }
        var now = _clock.GetUtcNow();
        if (row.Status != TenantExportStatus.Completed)
        {
            _logger.LogInformation(
                "TenantExport download blocked — export {ExportId} status is {Status}, not Completed.",
                exportId, row.Status);
            return null;
        }
        if (row.RevokedAt is not null)
        {
            _logger.LogInformation(
                "TenantExport download blocked — export {ExportId} was revoked at {RevokedAt}.",
                exportId, row.RevokedAt);
            return null;
        }
        if (row.ExpiresAt is not null && now >= row.ExpiresAt.Value)
        {
            _logger.LogInformation(
                "TenantExport download blocked — export {ExportId} expired at {ExpiresAt}.",
                exportId, row.ExpiresAt);
            return null;
        }
        if (string.IsNullOrWhiteSpace(row.ArtifactPath))
        {
            _logger.LogWarning(
                "TenantExport download blocked — export {ExportId} marked Completed but ArtifactPath is empty.",
                exportId);
            return null;
        }
        if (requestingUserId == Guid.Empty)
        {
            // Caller chain (the Razor page) must populate the user id;
            // we refuse Empty as a defensive belt-and-suspenders.
            _logger.LogWarning(
                "TenantExport download blocked — empty user id for export {ExportId}.",
                exportId);
            return null;
        }

        // Open the artifact before bumping the counter so a missing
        // artifact doesn't leave a phantom download. Sprint 51 / Phase
        // E — route through ITenantExportStorage when configured;
        // legacy callers without storage registered fall back to
        // direct File IO so existing tests stay green.
        Stream? stream;
        if (_storage is not null)
        {
            stream = await _storage.OpenReadAsync(row.ArtifactPath, ct);
        }
        else if (File.Exists(row.ArtifactPath))
        {
            stream = new FileStream(
                row.ArtifactPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
        }
        else
        {
            stream = null;
        }
        if (stream is null)
        {
            _logger.LogWarning(
                "TenantExport download blocked — export {ExportId} marked Completed but artifact missing at {Path}.",
                exportId, row.ArtifactPath);
            return null;
        }

        row.DownloadCount += 1;
        row.LastDownloadedAt = now;
        await _db.SaveChangesAsync(ct);

        var sha = row.ArtifactSha256 is null ? string.Empty : Convert.ToHexString(row.ArtifactSha256);
        await EmitTenantEventAsync(row.TenantId,
            "nickerp.tenancy.tenant_export_downloaded",
            JsonSerializer.SerializeToElement(new
            {
                exportId = row.Id,
                tenantId = row.TenantId,
                row.DownloadCount,
                requestingUserId
            }),
            requestingUserId, ct);

        var fileName = $"tenant-{row.TenantId}-export-{row.Id:N}.zip";
        // stream.Length isn't always available (HTTP streams from S3
        // can throw NotSupportedException). Prefer the row's recorded
        // size; fall back to stream.Length only when the stream
        // supports it.
        long resolvedSize = 0;
        if (row.ArtifactSizeBytes.HasValue)
        {
            resolvedSize = row.ArtifactSizeBytes.Value;
        }
        else if (stream.CanSeek)
        {
            try { resolvedSize = stream.Length; }
            catch (NotSupportedException) { resolvedSize = 0; }
        }
        return new TenantExportDownload(
            Stream: stream,
            ContentType: "application/zip",
            FileName: fileName,
            SizeBytes: resolvedSize,
            Sha256Hex: sha);
    }

    /// <inheritdoc />
    public async Task RevokeExportAsync(Guid exportId, Guid revokingUserId, CancellationToken ct = default)
    {
        if (revokingUserId == Guid.Empty)
        {
            throw new ArgumentException("RevokingUserId is required.", nameof(revokingUserId));
        }

        var row = await _db.TenantExportRequests
            .FirstOrDefaultAsync(r => r.Id == exportId, ct);
        if (row is null)
        {
            throw new InvalidOperationException($"Export {exportId} not found.");
        }
        if (row.Status == TenantExportStatus.Revoked)
        {
            return; // idempotent
        }

        var now = _clock.GetUtcNow();
        var prevStatus = row.Status;
        row.Status = TenantExportStatus.Revoked;
        row.RevokedAt = now;
        row.RevokedByUserId = revokingUserId;
        await _db.SaveChangesAsync(ct);

        // Best-effort artifact cleanup. A failed delete leaves the
        // artifact behind and the row marked Revoked; the sweeper will
        // pick it up. Don't fail the call on filesystem / S3 errors.
        // Sprint 51 / Phase E — route through ITenantExportStorage
        // when configured.
        if (!string.IsNullOrWhiteSpace(row.ArtifactPath))
        {
            try
            {
                if (_storage is not null)
                {
                    await _storage.DeleteAsync(row.ArtifactPath, ct);
                }
                else if (File.Exists(row.ArtifactPath))
                {
                    File.Delete(row.ArtifactPath);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "TenantExport revoke — failed to delete artifact at {Path} for export {ExportId}.",
                    row.ArtifactPath, exportId);
            }
        }

        await EmitTenantEventAsync(row.TenantId,
            "nickerp.tenancy.tenant_export_revoked",
            JsonSerializer.SerializeToElement(new
            {
                exportId = row.Id,
                tenantId = row.TenantId,
                previousStatus = prevStatus.ToString(),
                revokingUserId
            }),
            revokingUserId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantExportRequest>> ListExportsAsync(long tenantId, CancellationToken ct = default)
    {
        return await _db.TenantExportRequests
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.RequestedAt)
            .Take(50) // hard cap so the admin UI doesn't accidentally pull a year of history
            .ToListAsync(ct);
    }

    /// <summary>
    /// Sprint 51 / Phase B — best-effort NOTIFY emit on
    /// <see cref="TenantExportRunner.NotifyChannel"/>. Skipped silently
    /// when the underlying connection is not Postgres (in-memory
    /// provider in tests). The 30 s poll on the runner remains as
    /// the fallback so a missed notification does not strand a
    /// Pending row.
    /// </summary>
    private async Task TryNotifyRequestedAsync(Guid exportId, CancellationToken ct)
    {
        try
        {
            // Detect the in-memory provider; SQL execution against it
            // throws. The provider name check is the cheapest way.
            if (!_db.Database.IsNpgsql())
            {
                return;
            }
            // Channel name + payload are bounded ASCII. The payload is
            // the export id only — listeners look the row up themselves
            // so payload size stays under Postgres's 8 KB NOTIFY cap.
            var channel = TenantExportRunner.NotifyChannel;
            var payload = exportId.ToString("N");
            // NOTIFY identifiers + payloads must be inline-quoted; EF's
            // ExecuteSqlRawAsync without parameters is fine because both
            // values are constructed by us, not user input.
            var sql = $"NOTIFY {channel}, '{payload}';";
            await _db.Database.ExecuteSqlRawAsync(sql, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "TenantExport NOTIFY emit failed for export {ExportId}; falling back to poll-driven pickup.",
                exportId);
        }
    }

    private async Task EmitAsync(Tenant tenant, string eventType, JsonElement payload, Guid? actorUserId, CancellationToken ct)
    {
        try
        {
            var key = IdempotencyKey.ForEntityChange(
                tenantId: tenant.Id,
                eventType: eventType,
                entityType: nameof(TenantExportRequest),
                entityId: payload.TryGetProperty("exportId", out var idProp) ? idProp.ToString() : tenant.Id.ToString(),
                occurredAt: _clock.GetUtcNow());
            var evt = DomainEvent.Create(
                tenantId: tenant.Id,
                actorUserId: actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: eventType,
                entityType: nameof(TenantExportRequest),
                entityId: payload.TryGetProperty("exportId", out var idProp2) ? idProp2.ToString() : tenant.Id.ToString(),
                payload: payload,
                idempotencyKey: key,
                clock: _clock);
            await _publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            // Audit emission is best-effort.
            _logger.LogWarning(ex,
                "Failed to publish {EventType} for tenant {TenantId}; export operation persisted.",
                eventType, tenant.Id);
        }
    }

    private async Task EmitTenantEventAsync(long tenantId, string eventType, JsonElement payload, Guid? actorUserId, CancellationToken ct)
    {
        try
        {
            var key = IdempotencyKey.ForEntityChange(
                tenantId: tenantId,
                eventType: eventType,
                entityType: nameof(TenantExportRequest),
                entityId: payload.TryGetProperty("exportId", out var idProp) ? idProp.ToString() : tenantId.ToString(),
                occurredAt: _clock.GetUtcNow());
            var evt = DomainEvent.Create(
                tenantId: tenantId,
                actorUserId: actorUserId,
                correlationId: System.Diagnostics.Activity.Current?.RootId,
                eventType: eventType,
                entityType: nameof(TenantExportRequest),
                entityId: payload.TryGetProperty("exportId", out var idProp2) ? idProp2.ToString() : tenantId.ToString(),
                payload: payload,
                idempotencyKey: key,
                clock: _clock);
            await _publisher.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish {EventType} for tenant {TenantId}; export operation persisted.",
                eventType, tenantId);
        }
    }
}

/// <summary>
/// Sprint 25 — bundle-builder helper. Pulled into a static class so the
/// runner stays small and the DB-touching read logic is isolated for
/// testing. Not registered in DI.
/// </summary>
public static class TenantExportBundleBuilder
{
    /// <summary>Tables on <c>nickerp_inspection</c> the bundle reads
    /// when <see cref="TenantExportScope"/> is All or InspectionOnly.</summary>
    public static readonly IReadOnlyList<string> InspectionTablesDefault = new[]
    {
        "inspection.cases",
        "inspection.locations",
        "inspection.scanner_device_instances",
        "inspection.external_system_instances",
        "inspection.analysis_services",
        "inspection.location_assignments",
    };

    /// <summary>Tables on <c>nickerp_nickfinance</c> the bundle reads
    /// when scope is All or FinanceOnly.</summary>
    public static readonly IReadOnlyList<string> NickFinanceTablesDefault = new[]
    {
        "nickfinance.voucher",
        "nickfinance.petty_cash_box",
        "nickfinance.period",
    };

    /// <summary>Tables on <c>nickerp_platform</c> audit schema.</summary>
    public static readonly IReadOnlyList<string> AuditTablesDefault = new[]
    {
        "audit.events",
        "audit.notifications",
    };

    /// <summary>Tables on <c>nickerp_platform</c> identity schema.</summary>
    public static readonly IReadOnlyList<string> IdentityTablesDefault = new[]
    {
        "identity.identity_users",
        "identity.user_scopes",
    };

    /// <summary>
    /// Build the bundle to <paramref name="outputPath"/>. Sequential per
    /// DB; sets <c>app.tenant_id = '&lt;tenantId&gt;'</c> on each
    /// connection so RLS narrows reads to that tenant.
    /// </summary>
    /// <returns>(size in bytes, sha256 of the file).</returns>
    public static async Task<(long SizeBytes, byte[] Sha256)> BuildAsync(
        string outputPath,
        long tenantId,
        TenantExportRequest request,
        TenantExportOptions options,
        ILogger logger,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Build into a temp path then move into place — avoids leaving
        // a half-written zip if the runner crashes mid-build.
        var tempPath = outputPath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
        {
            var manifest = new
            {
                exportId = request.Id,
                tenantId,
                requestedAt = request.RequestedAt,
                requestedByUserId = request.RequestedByUserId,
                format = request.Format.ToString(),
                scope = request.Scope.ToString(),
                bundleVersion = "1.0",
                generatedAt = DateTimeOffset.UtcNow,
            };
            await WriteEntryAsync(zip, "manifest.json",
                JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }),
                ct);

            // Inspection block
            if (request.Scope is TenantExportScope.All or TenantExportScope.InspectionOnly)
            {
                if (!string.IsNullOrWhiteSpace(options.InspectionConnectionString))
                {
                    await ExportDbAsync(
                        zip,
                        "inspection",
                        options.InspectionConnectionString!,
                        options.InspectionTables ?? InspectionTablesDefault,
                        tenantId,
                        request.Format,
                        logger,
                        ct);
                }
                else
                {
                    logger.LogInformation("TenantExport[inspection] skipped — no connection string configured.");
                }
            }

            // NickFinance block
            if (request.Scope is TenantExportScope.All or TenantExportScope.FinanceOnly)
            {
                if (!string.IsNullOrWhiteSpace(options.NickFinanceConnectionString))
                {
                    await ExportDbAsync(
                        zip,
                        "nickfinance",
                        options.NickFinanceConnectionString!,
                        options.NickFinanceTables ?? NickFinanceTablesDefault,
                        tenantId,
                        request.Format,
                        logger,
                        ct);
                }
                else
                {
                    logger.LogInformation("TenantExport[nickfinance] skipped — no connection string configured.");
                }
            }

            // Platform block (audit + identity). When scope =
            // IdentityAndAudit we ONLY read these. When scope = All we
            // read these too.
            if (request.Scope is TenantExportScope.All or TenantExportScope.IdentityAndAudit)
            {
                if (!string.IsNullOrWhiteSpace(options.PlatformConnectionString))
                {
                    var platformTables = (options.IdentityTables ?? IdentityTablesDefault)
                        .Concat(options.AuditTables ?? AuditTablesDefault)
                        .ToList();
                    await ExportDbAsync(
                        zip,
                        "platform",
                        options.PlatformConnectionString!,
                        platformTables,
                        tenantId,
                        request.Format,
                        logger,
                        ct);
                }
                else
                {
                    logger.LogInformation("TenantExport[platform] skipped — no connection string configured.");
                }
            }
        }

        // Compute sha256 of the finished file. Two passes is fine — the
        // bundles are small enough that the IO overhead is negligible.
        long size = new FileInfo(tempPath).Length;
        byte[] sha;
        await using (var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
        using (var sha256 = SHA256.Create())
        {
            sha = await sha256.ComputeHashAsync(fs, ct);
        }

        // Move into place atomically. On Windows File.Move with overwrite
        // is OK; we also clean up any pre-existing target first to be
        // explicit.
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        File.Move(tempPath, outputPath);

        return (size, sha);
    }

    private static async Task ExportDbAsync(
        ZipArchive zip,
        string label,
        string connectionString,
        IReadOnlyList<string> tables,
        long tenantId,
        TenantExportFormat format,
        ILogger logger,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Per the Sprint 18 orchestrator: SET app.tenant_id explicitly
        // so the per-table RLS USING clauses admit the read. Don't use
        // SetSystemContext — that's only for the audit / opt-in tables.
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET app.tenant_id = '{tenantId}'; SET app.user_id = '00000000-0000-0000-0000-000000000000';";
            await setCmd.ExecuteNonQueryAsync(ct);
        }

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entryName = format switch
                {
                    TenantExportFormat.JsonBundle => $"{label}/{table}.json",
                    TenantExportFormat.CsvFlat => $"{label}/{table}.csv",
                    TenantExportFormat.Sql => $"{label}/{table}.sql",
                    _ => $"{label}/{table}.json"
                };
                var bytes = format switch
                {
                    TenantExportFormat.JsonBundle => await ReadTableAsJsonAsync(conn, table, tenantId, ct),
                    TenantExportFormat.CsvFlat => await ReadTableAsCsvAsync(conn, table, tenantId, ct),
                    TenantExportFormat.Sql => await ReadTableAsSqlAsync(conn, table, tenantId, ct),
                    _ => await ReadTableAsJsonAsync(conn, table, tenantId, ct)
                };
                await WriteEntryAsync(zip, entryName, bytes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "TenantExport[{Label}] failed reading {Table}; recording empty entry and continuing.",
                    label, table);
                var entryName = $"{label}/{table}.error.txt";
                var msg = Encoding.UTF8.GetBytes($"Failed to read {table}: {ex.GetType().Name}: {ex.Message}");
                await WriteEntryAsync(zip, entryName, msg, ct);
            }
        }
    }

    private static async Task<byte[]> ReadTableAsJsonAsync(
        NpgsqlConnection conn, string table, long tenantId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // Wrap rows as a JSON array via to_jsonb. Postgres does the
        // serialisation; we don't need to reflect over column types.
        cmd.CommandText =
            $"SELECT COALESCE(jsonb_agg(to_jsonb(t)), '[]'::jsonb)::text FROM {table} t WHERE t.\"TenantId\" = @tid;";
        var p = cmd.CreateParameter();
        p.ParameterName = "tid";
        p.Value = tenantId;
        cmd.Parameters.Add(p);
        var json = (string?)await cmd.ExecuteScalarAsync(ct) ?? "[]";
        return Encoding.UTF8.GetBytes(json);
    }

    private static async Task<byte[]> ReadTableAsCsvAsync(
        NpgsqlConnection conn, string table, long tenantId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE \"TenantId\" = @tid;";
        var p = cmd.CreateParameter();
        p.ParameterName = "tid";
        p.Value = tenantId;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var sb = new StringBuilder();
        // Header row.
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(QuoteCsv(reader.GetName(i)));
        }
        sb.Append('\n');
        while (await reader.ReadAsync(ct))
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) sb.Append(',');
                var value = reader.IsDBNull(i) ? string.Empty : Convert.ToString(reader.GetValue(i)) ?? string.Empty;
                sb.Append(QuoteCsv(value));
            }
            sb.Append('\n');
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static async Task<byte[]> ReadTableAsSqlAsync(
        NpgsqlConnection conn, string table, long tenantId, CancellationToken ct)
    {
        // pg_dump-ish output. INSERT INTO {table} VALUES (...);
        // No DROP / CREATE — re-importable into a schema that already
        // has the tables. JSON columns serialise as Postgres-quoted
        // string literals; consumers can re-parse.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE \"TenantId\" = @tid;";
        var p = cmd.CreateParameter();
        p.ParameterName = "tid";
        p.Value = tenantId;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var sb = new StringBuilder();
        sb.Append("-- TenantExport: ").Append(table).Append(" (tenant ").Append(tenantId).Append(")\n");
        var columnNames = new List<string>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            columnNames.Add($"\"{reader.GetName(i)}\"");
        }
        var columnList = string.Join(",", columnNames);

        while (await reader.ReadAsync(ct))
        {
            sb.Append("INSERT INTO ").Append(table).Append(" (").Append(columnList).Append(") VALUES (");
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (i > 0) sb.Append(',');
                if (reader.IsDBNull(i))
                {
                    sb.Append("NULL");
                }
                else
                {
                    var v = reader.GetValue(i);
                    sb.Append(QuoteSql(v));
                }
            }
            sb.Append(");\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string QuoteCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string QuoteSql(object v)
    {
        // Numbers / bools render bare; everything else gets E'...' with
        // \\' escapes. JSON columns come back as strings; that's fine —
        // the round-trip parser sees a quoted text literal.
        switch (v)
        {
            case bool b: return b ? "TRUE" : "FALSE";
            case short or int or long or float or double or decimal:
                return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!;
            case DateTime dt:
                return $"'{dt:O}'";
            case DateTimeOffset dto:
                return $"'{dto:O}'";
            case Guid g:
                return $"'{g}'";
            case byte[] bytes:
                return $"'\\\\x{Convert.ToHexString(bytes)}'";
            default:
                var s = Convert.ToString(v) ?? string.Empty;
                return "E'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }
    }

    private static async Task WriteEntryAsync(ZipArchive zip, string name, byte[] bytes, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var es = entry.Open();
        await es.WriteAsync(bytes, ct);
    }
}
