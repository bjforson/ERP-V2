using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using Npgsql;

namespace NickERP.Inspection.Application.Thresholds;

/// <summary>
/// Per-scanner threshold resolver (§6.5.3).
///
/// <para>
/// Three layers, in order: an in-process <see cref="ConcurrentDictionary{Guid,
/// CacheEntry}"/> cache; a long-lived Postgres
/// <c>LISTEN threshold_profile_updated</c> subscription that evicts on
/// receipt; and a 1-hour belt-and-braces TTL on every cache entry to
/// catch missed notifications (§6.5.8).
/// </para>
///
/// <para>
/// One instance is registered as a singleton, then resolved into three
/// DI slots — <see cref="IScannerThresholdResolver"/> (hot path),
/// <see cref="IHostedService"/> (LISTEN loop), and the health-check
/// reflection (see <see cref="ScannerThresholdResolverHealthCheck"/>).
/// Mirrors the v2 pattern <c>PreRenderWorker</c> uses for the same
/// reason: ONE worker instance, multiple resolved facets.
/// </para>
///
/// <para>
/// The hot path takes no DbContext dependency — every cache miss opens a
/// short-lived scope from the captured <see cref="IServiceScopeFactory"/>,
/// queries via <see cref="InspectionDbContext"/> with system tenant
/// context to bypass RLS (the resolver is a cross-tenant component;
/// tenant scoping is enforced by the caller).
/// </para>
/// </summary>
public sealed class ScannerThresholdResolver
    : IScannerThresholdResolver, IHostedService, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _configuration;
    private readonly IOptions<ScannerThresholdOptions> _options;
    private readonly ILogger<ScannerThresholdResolver> _logger;
    private readonly TimeProvider _clock;

    private readonly ConcurrentDictionary<Guid, CacheEntry> _cache = new();

    // ListenLoopAsync supplies these — guarded so the health check sees
    // a coherent view even when a reconnect is in flight.
    private readonly object _stateLock = new();
    private DateTimeOffset? _listenConnectedAt;
    private DateTimeOffset? _listenLastDisconnectedAt;
    private string? _listenLastError;
    private long _notificationsReceived;

    private CancellationTokenSource? _stopCts;
    private Task? _listenLoop;

    /// <summary>For the migration runner / on-host start: connection string is read from <c>ConnectionStrings:Inspection</c>.</summary>
    private const string InspectionConnectionStringName = "Inspection";

    public ScannerThresholdResolver(
        IServiceScopeFactory scopes,
        IConfiguration configuration,
        IOptions<ScannerThresholdOptions> options,
        ILogger<ScannerThresholdResolver> logger,
        TimeProvider? clock = null)
    {
        _scopes = scopes;
        _configuration = configuration;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    // -------------------------------------------------------------------
    // IScannerThresholdResolver — hot path
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public async ValueTask<ScannerThresholdSnapshot> GetActiveAsync(
        Guid scannerDeviceInstanceId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        if (_cache.TryGetValue(scannerDeviceInstanceId, out var cached)
            && now - cached.LoadedAt < _options.Value.FallbackTtl)
        {
            _logger.LogDebug(
                "Threshold cache HIT scanner={ScannerId} version={Version}",
                scannerDeviceInstanceId, cached.Snapshot.Version);
            return cached.Snapshot;
        }

        if (cached is not null)
        {
            _logger.LogDebug(
                "Threshold cache MISS (TTL expired) scanner={ScannerId} loadedAt={LoadedAt}",
                scannerDeviceInstanceId, cached.LoadedAt);
        }
        else
        {
            _logger.LogDebug(
                "Threshold cache MISS (cold) scanner={ScannerId}",
                scannerDeviceInstanceId);
        }

        var snapshot = await LoadFromDbAsync(scannerDeviceInstanceId, ct);
        _cache[scannerDeviceInstanceId] = new CacheEntry(snapshot, _clock.GetUtcNow());
        return snapshot;
    }

    /// <summary>
    /// Loads the active row for one scanner. Issued under
    /// <c>ITenantContext.SetSystemContext()</c> because the resolver is
    /// cross-tenant — analyst-facing UIs and per-scan hot paths both
    /// call it, and the scanner-id is sufficient to scope.
    /// </summary>
    private async Task<ScannerThresholdSnapshot> LoadFromDbAsync(
        Guid scannerDeviceInstanceId, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // Cross-tenant access — the resolver is shared infrastructure and
        // doesn't know which tenant owns the scanner up front. The caller's
        // request-scoped tenant context is unavailable to this scope (we
        // own it), and RLS would block the SELECT without a sentinel.
        var tenant = sp.GetRequiredService<NickERP.Platform.Tenancy.ITenantContext>();
        tenant.SetSystemContext();

        var db = sp.GetRequiredService<InspectionDbContext>();

        var row = await db.ScannerThresholdProfiles
            .AsNoTracking()
            .Where(p => p.ScannerDeviceInstanceId == scannerDeviceInstanceId
                        && p.Status == ScannerThresholdProfileStatus.Active)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            _logger.LogWarning(
                "No active threshold profile for scanner {ScannerId} — falling back to v1 defaults.",
                scannerDeviceInstanceId);
            return ScannerThresholdSnapshot.V1Defaults();
        }

        return ParseSnapshot(row.Version, row.ValuesJson);
    }

    /// <summary>
    /// Parses <c>ValuesJson</c> into <see cref="ScannerThresholdSnapshot"/>,
    /// merging missing keys with the v1 defaults (§6.5.8 schema-drift
    /// guard). Tolerant of the §6.5.2 grouped-keys layout
    /// (<c>edge_detection.canny_low</c>, etc.) and of a flat layout.
    /// </summary>
    internal static ScannerThresholdSnapshot ParseSnapshot(int version, string valuesJson)
    {
        var defaults = ScannerThresholdSnapshot.V1Defaults(version);
        if (string.IsNullOrWhiteSpace(valuesJson)) return defaults;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(valuesJson);
        }
        catch (JsonException)
        {
            return defaults;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return defaults;

            // Per §6.5.2 the persisted shape is grouped:
            //   edge_detection.canny_low, normalization.percentile_low, …
            // We also accept a flat layout for forward compatibility with
            // any future flattening migration.
            var edge = TryGetSection(root, "edge_detection");
            var norm = TryGetSection(root, "normalization");
            var split = TryGetSection(root, "split_consensus");
            var watch = TryGetSection(root, "watchdogs");
            var dec = TryGetSection(root, "decoder_limits");

            return new ScannerThresholdSnapshot(
                Version: version,
                CannyLow: ReadInt(edge, "canny_low") ?? ReadInt(root, "canny_low") ?? defaults.CannyLow,
                CannyHigh: ReadInt(edge, "canny_high") ?? ReadInt(root, "canny_high") ?? defaults.CannyHigh,
                PercentileLow: ReadDouble(norm, "percentile_low") ?? ReadDouble(root, "percentile_low") ?? defaults.PercentileLow,
                PercentileHigh: ReadDouble(norm, "percentile_high") ?? ReadDouble(root, "percentile_high") ?? defaults.PercentileHigh,
                SplitDisagreementGuardPx: ReadInt(split, "disagreement_guard_px") ?? ReadInt(root, "split_disagreement_guard_px") ?? defaults.SplitDisagreementGuardPx,
                PendingWithoutImagesHours: ReadInt(watch, "pending_without_images_hours") ?? ReadInt(root, "pending_without_images_hours") ?? defaults.PendingWithoutImagesHours,
                MaxImageDimPx: ReadInt(dec, "max_image_dim_px") ?? ReadInt(root, "max_image_dim_px") ?? defaults.MaxImageDimPx);
        }
    }

    private static JsonElement? TryGetSection(JsonElement root, string name)
        => root.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object
            ? section
            : null;

    private static int? ReadInt(JsonElement? section, string key)
    {
        if (section is null) return null;
        if (!section.Value.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    }

    private static double? ReadDouble(JsonElement? section, string key)
    {
        if (section is null) return null;
        if (!section.Value.TryGetProperty(key, out var v)) return null;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;
    }

    // -------------------------------------------------------------------
    // IHostedService — LISTEN/NOTIFY long-lived subscription
    // -------------------------------------------------------------------

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopCts = new CancellationTokenSource();
        _listenLoop = Task.Run(() => ListenLoopAsync(_stopCts.Token));
        _logger.LogInformation(
            "ScannerThresholdResolver started — listening on '{Channel}', fallback TTL={Ttl}",
            _options.Value.NotifyChannel, _options.Value.FallbackTtl);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopCts is null) return;
        try
        {
            _stopCts.Cancel();
            if (_listenLoop is not null) await _listenLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ScannerThresholdResolver shutdown non-fatal error.");
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var connStr = _configuration.GetConnectionString(InspectionConnectionStringName);
        if (string.IsNullOrEmpty(connStr))
        {
            _logger.LogError(
                "ConnectionStrings:Inspection is empty — ScannerThresholdResolver cannot subscribe to '{Channel}'. "
                + "Threshold updates won't propagate via NOTIFY; cache entries will only refresh on the {Ttl} TTL.",
                _options.Value.NotifyChannel, _options.Value.FallbackTtl);
            lock (_stateLock) { _listenLastError = "missing connection string"; }
            return;
        }

        var backoff = _options.Value.ListenReconnectInitialBackoff;
        var channel = _options.Value.NotifyChannel;

        while (!ct.IsCancellationRequested)
        {
            NpgsqlConnection? conn = null;
            try
            {
                conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                conn.Notification += OnNotification;

                await using (var cmd = new NpgsqlCommand($"LISTEN {QuoteIdentifier(channel)};", conn))
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                lock (_stateLock)
                {
                    _listenConnectedAt = _clock.GetUtcNow();
                    _listenLastError = null;
                }
                backoff = _options.Value.ListenReconnectInitialBackoff;
                _logger.LogInformation("Subscribed to Postgres LISTEN/{Channel}.", channel);

                // WaitAsync blocks until a notification arrives; re-arm
                // the loop so we keep pumping. Cancellation here is the
                // shutdown path (StopAsync cancelled `ct`).
                while (!ct.IsCancellationRequested)
                {
                    await conn.WaitAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _listenConnectedAt = null;
                    _listenLastDisconnectedAt = _clock.GetUtcNow();
                    _listenLastError = $"{ex.GetType().Name}: {ex.Message}";
                }
                _logger.LogWarning(ex,
                    "LISTEN/{Channel} dropped — reconnecting in {Backoff}.",
                    channel, backoff);
                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromMilliseconds(Math.Min(
                    backoff.TotalMilliseconds * 2,
                    _options.Value.ListenReconnectMaxBackoff.TotalMilliseconds));
            }
            finally
            {
                if (conn is not null)
                {
                    conn.Notification -= OnNotification;
                    try { await conn.DisposeAsync(); } catch { /* best-effort */ }
                }
            }
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        Interlocked.Increment(ref _notificationsReceived);
        // Payload format: "<scannerId>,<version>" — see §6.5.3 trigger.
        // Tolerate alternative formats by attempting a Guid parse on the
        // first comma-separated token.
        var payload = e.Payload ?? string.Empty;
        var commaIx = payload.IndexOf(',');
        var scannerToken = commaIx > 0 ? payload[..commaIx] : payload;
        if (Guid.TryParse(scannerToken, out var scannerId))
        {
            if (_cache.TryRemove(scannerId, out _))
            {
                _logger.LogDebug(
                    "Threshold cache EVICT scanner={ScannerId} (NOTIFY {Channel} payload='{Payload}')",
                    scannerId, _options.Value.NotifyChannel, payload);
            }
        }
        else
        {
            _logger.LogWarning(
                "NOTIFY {Channel} payload not parseable as scannerId: '{Payload}' — full cache flush as a safety net.",
                _options.Value.NotifyChannel, payload);
            _cache.Clear();
        }
    }

    private static string QuoteIdentifier(string identifier)
    {
        // Channel names are operator-supplied configuration; reject
        // anything outside [A-Za-z0-9_] to forestall a SQL-shaped payload
        // sneaking through. Postgres LISTEN names are restricted anyway.
        foreach (var c in identifier)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                throw new ArgumentException(
                    $"Invalid LISTEN channel '{identifier}' — only [A-Za-z0-9_] allowed.",
                    nameof(identifier));
        }
        return identifier;
    }

    // -------------------------------------------------------------------
    // Health-check support
    // -------------------------------------------------------------------

    /// <summary>Snapshot of LISTEN/NOTIFY connection state. Read by the health check.</summary>
    public ListenState GetListenState()
    {
        lock (_stateLock)
        {
            return new ListenState(
                IsConnected: _listenConnectedAt is not null,
                ConnectedAt: _listenConnectedAt,
                LastDisconnectedAt: _listenLastDisconnectedAt,
                LastError: _listenLastError,
                NotificationsReceived: Interlocked.Read(ref _notificationsReceived),
                CacheEntries: _cache.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stopCts?.Cancel();
        _stopCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record CacheEntry(ScannerThresholdSnapshot Snapshot, DateTimeOffset LoadedAt);

    /// <summary>State exposed to the health check.</summary>
    public sealed record ListenState(
        bool IsConnected,
        DateTimeOffset? ConnectedAt,
        DateTimeOffset? LastDisconnectedAt,
        string? LastError,
        long NotificationsReceived,
        int CacheEntries);
}
