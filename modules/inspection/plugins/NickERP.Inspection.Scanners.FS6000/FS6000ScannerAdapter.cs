using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.Scanners.FS6000;

/// <summary>
/// Real FS6000 scanner adapter. Watches a configured directory for completed
/// scan sets — each scan is three sibling .img files with a shared stem
/// (<c>{stem}high.img</c>, <c>{stem}low.img</c>, <c>{stem}material.img</c>)
/// — and surfaces them through the <see cref="IScannerAdapter"/> contract.
///
/// One <see cref="RawScanArtifact"/> per completed scan set:
///   - <c>SourcePath</c> = absolute stem (the prefix the three files share).
///   - <c>Format</c> = <c>vendor/fs6000</c>.
///   - <c>Bytes</c> = JSON manifest pointing back to the three sibling files +
///     hashes, so <see cref="ParseAsync"/> can re-read them deterministically
///     without re-scanning the directory.
///
/// <see cref="ParseAsync"/> reads the three sibling files, runs the ported
/// <see cref="FS6000FormatDecoder"/>, renders a percentile-normalized
/// 8-bit grayscale PNG from the high-energy channel, and returns it as the
/// <see cref="ParsedArtifact"/>. Width/height come from the decoder; raw
/// channel paths and the original timestamp are surfaced in
/// <see cref="ParsedArtifact.Metadata"/> so the host can hand them to the
/// pre-render service later (ROADMAP §4.3) for the full analyst viewer.
///
/// Deduplication is in-process only — the adapter remembers stems it has
/// already emitted in the current run. Real durable dedup is the host's job
/// (via <c>Scan.IdempotencyKey</c>); this just stops a polling loop from
/// re-emitting the same scan every cycle.
/// </summary>
[Plugin("fs6000")]
public sealed class FS6000ScannerAdapter : IScannerAdapter
{
    private const string HighSuffix = "high.img";
    private const string LowSuffix = "low.img";
    private const string MaterialSuffix = "material.img";

    /// <summary>
    /// Stems we've already emitted this run, keyed as <c>{tenantId}|{stem}</c>
    /// so two tenants pointing at the same physical watch path don't suppress
    /// each other's first emission (Sprint PT, contract 1.1). Bounded to
    /// avoid unbounded growth on long-lived processes.
    /// </summary>
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _seenLock = new();
    private const int SeenMaxEntries = 4096;

    public string TypeCode => "fs6000";

    public ScannerCapabilities Capabilities { get; } = new(
        SupportedFormats: new[] { "image/png" },
        SupportedModes: new[] { "high-energy", "low-energy", "material" },
        SupportsLiveStream: true,
        SupportsDualEnergy: true);

    public Task<ConnectionTestResult> TestAsync(ScannerDeviceConfig config, CancellationToken ct = default)
    {
        var cfg = ParseConfig(config);
        if (string.IsNullOrWhiteSpace(cfg.WatchPath))
            return Task.FromResult(new ConnectionTestResult(false, "WatchPath not configured."));

        if (!Directory.Exists(cfg.WatchPath))
            return Task.FromResult(new ConnectionTestResult(false, $"WatchPath does not exist: {cfg.WatchPath}"));

        try
        {
            // Pure read probe — count how many scan sets are already there.
            var sets = EnumerateScanSets(cfg.WatchPath).Take(50).Count();
            return Task.FromResult(new ConnectionTestResult(
                Success: true,
                Message: $"Watching {cfg.WatchPath} — {sets} existing scan set(s) detected.",
                Latency: TimeSpan.FromMilliseconds(1)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ConnectionTestResult(false, $"Probe failed: {ex.Message}"));
        }
    }

    public async IAsyncEnumerable<RawScanArtifact> StreamAsync(
        ScannerDeviceConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cfg = ParseConfig(config);
        if (!Directory.Exists(cfg.WatchPath))
            throw new DirectoryNotFoundException(
                $"FS6000 WatchPath does not exist: {cfg.WatchPath}. Configure ScannerDeviceInstance.ConfigJson.WatchPath.");

        var poll = TimeSpan.FromSeconds(Math.Max(1, cfg.PollIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            foreach (var set in EnumerateScanSets(cfg.WatchPath))
            {
                if (ct.IsCancellationRequested) yield break;
                var seenKey = SeenKey(config.TenantId, set.Stem);
                if (!ShouldEmit(seenKey)) continue;

                RawScanArtifact? artifact = null;
                try
                {
                    artifact = BuildRawArtifact(config, set);
                }
                catch (IOException)
                {
                    // File is mid-write or briefly locked — skip this cycle, the
                    // next pass will pick it up.
                    Forget(seenKey);
                }

                if (artifact is not null)
                    yield return artifact;
            }

            try { await Task.Delay(poll, ct); }
            catch (TaskCanceledException) { yield break; }
        }
    }

    public async Task<ParsedArtifact> ParseAsync(RawScanArtifact raw, CancellationToken ct = default)
    {
        var manifest = JsonSerializer.Deserialize<ScanSetManifest>(raw.Bytes)
            ?? throw new InvalidDataException("FS6000 raw artifact is missing its manifest envelope.");

        var highBytes = await File.ReadAllBytesAsync(manifest.HighPath, ct);
        var lowBytes = await File.ReadAllBytesAsync(manifest.LowPath, ct);

        // Pull preview-tuning from the device config — fall back to sane
        // defaults if the host doesn't pipe the config through (e.g. when
        // ParseAsync is called outside a stream context).
        var pLow = manifest.PreviewPercentileLow ?? 1.0;
        var pHigh = manifest.PreviewPercentileHigh ?? 99.0;

        int width;
        int height;
        ushort[] high;
        DateTime? capturedHeader;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scanner.type"] = "fs6000",
            ["scanner.stem"] = manifest.Stem,
            ["channel.high.path"] = manifest.HighPath,
            ["channel.high.sha256"] = manifest.HighSha256,
            ["channel.low.path"] = manifest.LowPath,
            ["channel.low.sha256"] = manifest.LowSha256,
        };

        if (!string.IsNullOrEmpty(manifest.MaterialPath) && File.Exists(manifest.MaterialPath))
        {
            var materialBytes = await File.ReadAllBytesAsync(manifest.MaterialPath, ct);
            var decoded = FS6000FormatDecoder.Decode(highBytes, lowBytes, materialBytes);
            width = decoded.Width;
            height = decoded.Height;
            high = decoded.High;
            capturedHeader = decoded.Timestamp;
            metadata["channel.material.path"] = manifest.MaterialPath;
            metadata["channel.material.sha256"] = manifest.MaterialSha256 ?? "";
            metadata["channel.set"] = "high+low+material";
        }
        else
        {
            var (w, h, hi, _, ts) = FS6000FormatDecoder.DecodeEnergyOnly(highBytes, lowBytes);
            width = w;
            height = h;
            high = hi;
            capturedHeader = ts;
            metadata["channel.set"] = "high+low";
        }

        var pngBytes = FS6000PreviewRenderer.RenderHighEnergyPng(
            high, width, height, pLow, pHigh);

        if (capturedHeader is { } ts2)
            metadata["scanner.captured_at_header"] =
                new DateTimeOffset(ts2, TimeSpan.Zero).ToString("O");
        metadata["preview.percentile_low"] = pLow.ToString("0.##");
        metadata["preview.percentile_high"] = pHigh.ToString("0.##");

        return new ParsedArtifact(
            DeviceId: raw.DeviceId,
            CapturedAt: raw.CapturedAt,
            WidthPx: width,
            HeightPx: height,
            Channels: 1,
            MimeType: "image/png",
            Bytes: pngBytes,
            Metadata: metadata);
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
    /// Find every directory entry whose name matches the FS6000 high/low/material
    /// triplet pattern. Yields one record per stem regardless of channel
    /// completeness — caller decides whether material is required.
    /// </summary>
    private static IEnumerable<ScanSetFiles> EnumerateScanSets(string root)
    {
        // Group by stem in one directory pass. We look at any *high.img and
        // ask whether its sibling *low.img exists. Material is optional.
        var highs = Directory.EnumerateFiles(root, "*" + HighSuffix, SearchOption.AllDirectories);
        foreach (var highPath in highs)
        {
            var stem = StripSuffix(highPath, HighSuffix);
            if (stem is null) continue;

            var lowPath = stem + LowSuffix;
            if (!File.Exists(lowPath)) continue;

            var materialPath = stem + MaterialSuffix;
            yield return new ScanSetFiles(
                Stem: stem,
                HighPath: highPath,
                LowPath: lowPath,
                MaterialPath: File.Exists(materialPath) ? materialPath : null);
        }
    }

    private static string? StripSuffix(string path, string suffix)
    {
        if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return path[..^suffix.Length];
        return null;
    }

    /// <summary>
    /// Compose the <c>_seen</c>-set entry key. Tenant-prefixed so two tenants
    /// can share a physical watch path without one suppressing the other.
    /// </summary>
    private static string SeenKey(long tenantId, string stem) =>
        $"{tenantId}|{stem}";

    private bool ShouldEmit(string seenKey)
    {
        lock (_seenLock)
        {
            if (_seen.Contains(seenKey)) return false;
            if (_seen.Count >= SeenMaxEntries)
            {
                // Defensive: drop the oldest half. We don't bother with
                // strict LRU — long-lived hosts rotate naturally as the
                // scanner moves on to fresh stems.
                var trimTo = _seen.Take(SeenMaxEntries / 2).ToArray();
                _seen.Clear();
                foreach (var k in trimTo) _seen.Add(k);
            }
            _seen.Add(seenKey);
            return true;
        }
    }

    private void Forget(string seenKey)
    {
        lock (_seenLock) { _seen.Remove(seenKey); }
    }

    private RawScanArtifact BuildRawArtifact(ScannerDeviceConfig config, ScanSetFiles set)
    {
        var hSha = HashFile(set.HighPath);
        var lSha = HashFile(set.LowPath);
        string? mSha = set.MaterialPath is null ? null : HashFile(set.MaterialPath);

        // Surface the user's preview tuning into the manifest so ParseAsync
        // can use it without re-reading the device config.
        var deviceCfg = ParseConfig(config);

        var manifest = new ScanSetManifest(
            Stem: set.Stem,
            HighPath: set.HighPath,
            HighSha256: hSha,
            LowPath: set.LowPath,
            LowSha256: lSha,
            MaterialPath: set.MaterialPath,
            MaterialSha256: mSha,
            PreviewPercentileLow: deviceCfg.PreviewPercentileLow,
            PreviewPercentileHigh: deviceCfg.PreviewPercentileHigh);

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var capturedAt = File.GetLastWriteTimeUtc(set.HighPath);

        return new RawScanArtifact(
            DeviceId: config.DeviceId,
            SourcePath: set.Stem,
            CapturedAt: new DateTimeOffset(capturedAt, TimeSpan.Zero),
            Format: "vendor/fs6000",
            Bytes: manifestBytes);
    }

    private static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(fs);
        return Convert.ToHexString(hash);
    }

    // --- DTOs -----------------------------------------------------------

    /// <summary>Per-instance config blob deserialized from <see cref="ScannerDeviceConfig.ConfigJson"/>.</summary>
    private sealed class AdapterConfig
    {
        public string WatchPath { get; set; } = string.Empty;
        public int PollIntervalSeconds { get; set; } = 5;
        public double? PreviewPercentileLow { get; set; }
        public double? PreviewPercentileHigh { get; set; }
    }

    /// <summary>One discovered scan triplet — paths only, no IO.</summary>
    private sealed record ScanSetFiles(
        string Stem,
        string HighPath,
        string LowPath,
        string? MaterialPath);

    /// <summary>Manifest envelope serialized into <see cref="RawScanArtifact.Bytes"/>.</summary>
    private sealed record ScanSetManifest(
        string Stem,
        string HighPath,
        string HighSha256,
        string LowPath,
        string LowSha256,
        string? MaterialPath,
        string? MaterialSha256,
        double? PreviewPercentileLow,
        double? PreviewPercentileHigh);
}
