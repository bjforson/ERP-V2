using System.Runtime.CompilerServices;
using System.Text;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.Scanners.Mock;

/// <summary>
/// Synthetic scanner adapter used to validate the plugin loader, the
/// scan-ingestion pipeline, and the analyst review UI before real
/// hardware adapters land. Emits one synthetic artifact every
/// <c>EmitIntervalSeconds</c> (from instance config; default 30) until
/// the cancellation token fires.
/// </summary>
[Plugin("mock-scanner", Module = "inspection")]
public sealed class MockScannerAdapter : IScannerAdapter
{
    public string TypeCode => "mock-scanner";

    public ScannerCapabilities Capabilities { get; } = new(
        SupportedFormats: new[] { "image/png" },
        SupportedModes: new[] { "single-energy" },
        SupportsLiveStream: true,
        SupportsDualEnergy: false);

    public Task<ConnectionTestResult> TestAsync(ScannerDeviceConfig config, CancellationToken ct = default)
    {
        return Task.FromResult(new ConnectionTestResult(
            Success: true,
            Message: "Mock scanner — always reachable.",
            Latency: TimeSpan.FromMilliseconds(1)));
    }

    public async IAsyncEnumerable<RawScanArtifact> StreamAsync(
        ScannerDeviceConfig config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var interval = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            yield return SyntheticArtifact(config);
            try { await Task.Delay(interval, ct); }
            catch (TaskCanceledException) { yield break; }
        }
    }

    public Task<ParsedArtifact> ParseAsync(RawScanArtifact raw, CancellationToken ct = default)
    {
        // Synthetic 1x1 PNG (raw.Bytes already a "PNG"-magic stub from SyntheticArtifact).
        return Task.FromResult(new ParsedArtifact(
            DeviceId: raw.DeviceId,
            CapturedAt: raw.CapturedAt,
            WidthPx: 1,
            HeightPx: 1,
            Channels: 1,
            MimeType: "image/png",
            Bytes: raw.Bytes,
            Metadata: new Dictionary<string, string>
            {
                ["source"] = "mock",
                ["original-path"] = raw.SourcePath
            }));
    }

    private static RawScanArtifact SyntheticArtifact(ScannerDeviceConfig config)
    {
        return new RawScanArtifact(
            DeviceId: config.DeviceId,
            SourcePath: $"mock://{config.DeviceId}/{Guid.NewGuid():N}.png",
            CapturedAt: DateTimeOffset.UtcNow,
            Format: "image/png",
            Bytes: Encoding.ASCII.GetBytes("\x89PNG\r\n\x1a\n[mock]"));
    }
}
