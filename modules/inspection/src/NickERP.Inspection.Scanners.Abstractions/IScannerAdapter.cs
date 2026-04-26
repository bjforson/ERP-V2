namespace NickERP.Inspection.Scanners.Abstractions;

/// <summary>
/// Plugin contract every scanner adapter implements. Concrete classes are
/// decorated with <c>[NickERP.Platform.Plugins.Plugin("type-code")]</c> and
/// shipped as a sibling DLL + plugin.json under the inspection host's
/// plugins folder.
/// </summary>
public interface IScannerAdapter
{
    /// <summary>Stable code matching the <c>[Plugin]</c> attribute on the concrete class.</summary>
    string TypeCode { get; }

    /// <summary>Capabilities the host needs to know about (which formats, modes, etc. the scanner produces).</summary>
    ScannerCapabilities Capabilities { get; }

    /// <summary>Test connectivity with the configured scanner. Should be cheap; called from admin UI.</summary>
    Task<ConnectionTestResult> TestAsync(ScannerDeviceConfig config, CancellationToken ct = default);

    /// <summary>
    /// Stream raw scan artifacts as they land. Adapter is responsible for
    /// detecting new scans (file watcher, DB poll, SDK callback, etc.) and
    /// surfacing them to the host. Cancellation gracefully terminates.
    /// </summary>
    IAsyncEnumerable<RawScanArtifact> StreamAsync(ScannerDeviceConfig config, CancellationToken ct = default);

    /// <summary>
    /// Parse a raw artifact into a normalized form (image bytes, dimensions,
    /// channels, capture metadata). Vendor-specific format decoding lives
    /// here; the inspection core only sees the parsed form.
    /// </summary>
    Task<ParsedArtifact> ParseAsync(RawScanArtifact raw, CancellationToken ct = default);
}

/// <summary>What the scanner can do — surfaced to the admin UI when registering an instance.</summary>
public sealed record ScannerCapabilities(
    IReadOnlyList<string> SupportedFormats,
    IReadOnlyList<string> SupportedModes,
    bool SupportsLiveStream,
    bool SupportsDualEnergy);

/// <summary>Per-instance config blob the host passes to every adapter call.</summary>
public sealed record ScannerDeviceConfig(
    Guid DeviceId,
    Guid LocationId,
    Guid? StationId,
    string ConfigJson);

/// <summary>Outcome of a connectivity test.</summary>
public sealed record ConnectionTestResult(bool Success, string Message, TimeSpan? Latency = null);

/// <summary>One raw scan artifact emitted by an <see cref="IScannerAdapter.StreamAsync"/> producer.</summary>
public sealed record RawScanArtifact(
    Guid DeviceId,
    string SourcePath,
    DateTimeOffset CapturedAt,
    string Format,
    byte[] Bytes);

/// <summary>Adapter-parsed artifact in a vendor-neutral shape.</summary>
public sealed record ParsedArtifact(
    Guid DeviceId,
    DateTimeOffset CapturedAt,
    int WidthPx,
    int HeightPx,
    int Channels,
    string MimeType,
    byte[] Bytes,
    IReadOnlyDictionary<string, string> Metadata);
