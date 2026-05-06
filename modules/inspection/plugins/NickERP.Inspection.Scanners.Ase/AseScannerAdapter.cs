using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using NickERP.Inspection.Edge.Abstractions;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.Scanners.Ase;

/// <summary>
/// Sprint 50 / FU-ase-adapter-plugin — ASE scanner adapter, vendor-neutral
/// shape. Replaces v1's <c>AseBackgroundService</c> as a plugin under the
/// v2 platform contract. Both <see cref="IScannerAdapter"/> AND
/// <see cref="IScannerCursorSyncAdapter"/> are implemented so the
/// <c>AseSyncWorker</c> resolves a cursor-capable adapter via
/// <see cref="IPluginRegistry"/> and pulls batches without per-vendor
/// shimming.
///
/// <para>
/// <b>Stub-shaped real plugin.</b> The on-site ASE upstream is a remote
/// SQL Server table; vendor-specific protocol wiring (connection strings,
/// SQL, row → bytes mapping, etc.) lands when an ASE site is provisioned
/// (no Tema-class pilot today). Until then, this adapter ships the
/// CONTRACT shape so:
/// <list type="bullet">
///   <item><description><see cref="AseSyncWorker"/> resolves the plugin
///   via <see cref="IPluginRegistry"/> and persists records through the
///   cursor-sync path (Phase B) without conditional vendor branches.</description></item>
///   <item><description>Scanner-health sweep + plugin-listing UI surfaces
///   the adapter so ops sees which Tema-class scanners are wired.</description></item>
///   <item><description>Conformance tests exercise the canonical
///   <see cref="ScanPackage"/> shape — the same tamper-detection round-trip
///   FS6000 ships under.</description></item>
/// </list>
/// Real-protocol wiring is a TODO marked inline; until then
/// <see cref="PullAsync"/> returns an empty batch + the cursor unchanged
/// (the adapter is "alive but quiet"). That keeps the worker idle on
/// fresh deploys without the no-plugin warning.
/// </para>
///
/// <para>
/// <b>Vendor-neutral shape.</b> The canonical <see cref="ScanPackage"/>
/// the adapter emits never carries Ghana / FS6000 / Tema-specific field
/// names in its core; SiteId / GatewayId / OperatorId / ContainerNumber
/// stay empty here and get filled by the host post-parse from the
/// resolved <see cref="ScannerDeviceConfig"/> + edge-node context (same
/// posture as FS6000).
/// </para>
/// </summary>
[Plugin("ase", Module = "inspection")]
public sealed class AseScannerAdapter : IScannerAdapter, IScannerCursorSyncAdapter
{
    /// <inheritdoc />
    public string TypeCode => "ase";

    /// <summary>
    /// Capabilities surface the ASE upstream's typical multi-energy /
    /// dual-view shape. <see cref="ScannerCapabilities.SupportsLiveStream"/>
    /// is intentionally <c>false</c> — ASE is a cursor-pull source, not a
    /// stream — and <see cref="ScannerCapabilities.SupportsCalibrationMode"/>
    /// stays <c>false</c> until the calibration-replay path lands per
    /// §6.9.
    /// </summary>
    public ScannerCapabilities Capabilities { get; } = new(
        SupportedFormats: new[] { "image/png", "vendor/ase" },
        SupportedModes: new[] { "high-energy", "low-energy" },
        SupportsLiveStream: false,
        SupportsDualEnergy: true);

    /// <inheritdoc />
    public Task<ConnectionTestResult> TestAsync(ScannerDeviceConfig config, CancellationToken ct = default)
    {
        var cfg = ParseConfig(config);

        // Stub mode: no upstream connection string configured. Surface a
        // success result so /admin/scanners + the health-sweep worker
        // don't show the device as "unreachable" on every fresh deploy
        // — the ASE plugin is intentionally idle until an on-site row
        // ships ConnectionString.
        if (string.IsNullOrWhiteSpace(cfg.ConnectionString))
        {
            return Task.FromResult(new ConnectionTestResult(
                Success: true,
                Message: "ASE adapter idle (stub mode) — ConnectionString not configured.",
                Latency: TimeSpan.FromMilliseconds(1)));
        }

        // TODO when ASE on-site — issue a SELECT 1 against the configured
        // SQL Server connection. The vendor's exact ping query / driver
        // settings come from the deploy package; leave the probe stub-shaped
        // here so we can compile + ship the contract today without a
        // System.Data.SqlClient ProjectReference dragging into every host.
        return Task.FromResult(new ConnectionTestResult(
            Success: true,
            Message: "ASE adapter ConnectionString configured — real probe lands when ASE site is provisioned.",
            Latency: TimeSpan.FromMilliseconds(1)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ASE is a cursor-pull source; <see cref="StreamAsync"/> is required
    /// by the base contract but the worker that drives this adapter
    /// (<c>AseSyncWorker</c>) goes through <see cref="PullAsync"/>
    /// instead. Surface an empty stream so any caller that defaults to
    /// the streaming path (e.g. a misconfigured <c>ScannerIngestionWorker</c>)
    /// no-ops cleanly rather than throwing.
    /// </remarks>
    public async IAsyncEnumerable<RawScanArtifact> StreamAsync(
        ScannerDeviceConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ASE rows are emitted as byte payloads via <see cref="PullAsync"/>;
    /// the legacy <see cref="ParseAsync"/> overload exists for backward
    /// compatibility with the pre-Sprint-40 ingest path. The canonical
    /// path is <see cref="ParseScanAsync"/>.
    /// </remarks>
    public Task<ParsedArtifact> ParseAsync(RawScanArtifact raw, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Task.FromResult(new ParsedArtifact(
            DeviceId: raw.DeviceId,
            CapturedAt: raw.CapturedAt,
            WidthPx: 0,
            HeightPx: 0,
            Channels: 1,
            MimeType: raw.Format,
            Bytes: raw.Bytes,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scanner.type"] = "ase"
            },
            FormatVersion: "ase-v1"));
    }

    /// <summary>
    /// Sprint 50 / Phase A — canonical scan-package parse. Wraps the raw
    /// ASE bytes into a single-image <see cref="ScanPackage"/> bundle
    /// ready for HMAC signing by the host. Mirrors FS6000's single-blob
    /// path:
    /// <list type="bullet">
    ///   <item><description><c>ScannerId</c> ← <see cref="TypeCode"/> ("ase").</description></item>
    ///   <item><description><c>ScanType</c> ← <c>"primary"</c>.</description></item>
    ///   <item><description><c>OccurredAt</c> ← current UTC; the host
    ///   overrides from the cursor record's <c>CapturedAt</c> when
    ///   available.</description></item>
    ///   <item><description><c>SiteId</c> / <c>GatewayId</c> / <c>OperatorId</c> /
    ///   <c>ContainerNumber</c> / etc. — empty here. Host fills post-parse.</description></item>
    /// </list>
    /// Per-image sha256 is computed inline so the validator's recompute
    /// step passes; <see cref="ScanPackage.ManifestSha256"/> +
    /// <see cref="ScanPackage.ManifestSignature"/> stay empty for the
    /// host to <c>Seal</c> with the per-edge HMAC key.
    /// </summary>
    public Task<ParsedScan> ParseScanAsync(
        byte[] rawScanData,
        ScannerCapabilities capabilities,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawScanData);
        ArgumentNullException.ThrowIfNull(capabilities);

        var view = capabilities.SupportsDualEnergy ? "high-energy" : "primary";
        var image = new ImageFile(
            FileName: "scan.bin",
            Sha256Hex: ScanPackageManifest.Sha256Hex(rawScanData),
            View: view,
            SizeBytes: rawScanData.LongLength,
            Data: rawScanData);

        var package = new ScanPackage(
            ScanId: Guid.NewGuid().ToString(),
            SiteId: string.Empty,
            ScannerId: "ase",
            GatewayId: string.Empty,
            ScanType: "primary",
            OccurredAt: DateTimeOffset.UtcNow,
            OperatorId: string.Empty,
            ContainerNumber: string.Empty,
            VehiclePlate: string.Empty,
            DeclarationNumber: string.Empty,
            ManifestNumber: string.Empty,
            ImageFiles: new[] { image },
            ManifestSha256: Array.Empty<byte>(),
            ManifestSignature: Array.Empty<byte>());

        var artifact = new ParsedArtifact(
            DeviceId: Guid.Empty,
            CapturedAt: package.OccurredAt,
            WidthPx: 0,
            HeightPx: 0,
            Channels: 1,
            MimeType: "vendor/ase",
            Bytes: rawScanData,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["scanner.type"] = "ase",
                ["channel.set"] = "single-blob"
            },
            FormatVersion: "ase-v1");

        return Task.FromResult(new ParsedScan(new[] { artifact }, package));
    }

    /// <summary>
    /// Sprint 50 / Phase A — cursor-sync pull entry point.
    ///
    /// <para>
    /// <b>Stub behavior.</b> Without an upstream connection configured,
    /// the adapter returns an empty batch + the cursor unchanged
    /// (<see cref="CursorSyncBatch.HasMore"/> = <c>false</c>). The
    /// worker treats that as "no new data this cycle" and idles cleanly.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotency.</b> The contract requires every emitted record to
    /// carry a stable <see cref="CursorSyncRecord.IdempotencyKey"/>
    /// — even in the test-fed real-shape path below, the key is a
    /// SHA-256 of the synthetic source reference so the host's
    /// <c>ScanArtifact.IdempotencyKey</c> uniqueness dedupes replays.
    /// Real implementation will hash the upstream natural key (ASE
    /// row id + capture timestamp) the same way.
    /// </para>
    ///
    /// <para>
    /// TODO when ASE on-site — open the configured SQL Server
    /// connection, run the vendor SELECT (rows where row_id &gt;
    /// <paramref name="cursor"/>, ordered by row_id asc, top
    /// <paramref name="batchLimit"/>), map each row to a
    /// <see cref="CursorSyncRecord"/>, and return the largest row_id
    /// in <see cref="CursorSyncBatch.NextCursor"/>. Until then this
    /// method short-circuits.
    /// </para>
    /// </summary>
    public Task<CursorSyncBatch> PullAsync(
        ScannerDeviceConfig config,
        string cursor,
        int batchLimit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(cursor);
        if (batchLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchLimit),
                "batchLimit must be positive.");
        }

        // Stub mode — empty batch, cursor unchanged. Real adapter
        // overrides this in a later sprint when the ASE site is
        // provisioned.
        var empty = new CursorSyncBatch(
            Records: Array.Empty<CursorSyncRecord>(),
            NextCursor: cursor,
            HasMore: false);
        return Task.FromResult(empty);
    }

    // --- helpers --------------------------------------------------------

    private static AdapterConfig ParseConfig(ScannerDeviceConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ConfigJson))
            return new AdapterConfig();
        try
        {
            return JsonSerializer.Deserialize<AdapterConfig>(config.ConfigJson) ?? new AdapterConfig();
        }
        catch (JsonException)
        {
            return new AdapterConfig();
        }
    }

    /// <summary>
    /// Compute the canonical idempotency key for an ASE record. SHA-256
    /// of the source reference; stable across replays. Static so the
    /// real-protocol wiring (when it lands) can call the same helper for
    /// shape consistency with conformance tests.
    /// </summary>
    public static string ComputeIdempotencyKey(string sourceReference)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceReference);
        var bytes = System.Text.Encoding.UTF8.GetBytes(sourceReference);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>Per-instance config blob. Only ConnectionString is wired today; rest is reserved.</summary>
    private sealed class AdapterConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string ScanQuery { get; set; } = string.Empty;
        public int PollOverlapMinutes { get; set; } = 5;
    }
}
