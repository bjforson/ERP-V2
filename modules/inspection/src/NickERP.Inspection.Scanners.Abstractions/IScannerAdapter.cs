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

/// <summary>
/// What the scanner can do — surfaced to the admin UI when registering an
/// instance. New flags appended at the end with safe defaults so existing
/// adapters compile unchanged (additive-only contract evolution; see
/// IMAGE-ANALYSIS-MODERNIZATION.md §6.7.3, §6.9.9, §6.11.2 and Phase 7.0
/// contract-freeze prep).
/// </summary>
/// <param name="SupportedFormats">Vendor-neutral output formats (e.g. <c>image/png</c>, <c>vendor/fs6000</c>).</param>
/// <param name="SupportedModes">Modes the device exposes (e.g. <c>high-energy</c>, <c>material</c>, <c>calibration</c>).</param>
/// <param name="SupportsLiveStream">Adapter can stream artifacts as they land (true) versus poll-only (false).</param>
/// <param name="SupportsDualEnergy">Adapter exposes high+low energy channels.</param>
/// <param name="RawChannelsAvailable">
/// True iff <see cref="ParsedArtifact"/> carries the underlying high-energy
/// and low-energy channels (and material when present) rather than only an
/// 8-bit pseudocolor preview. Required for the §6.2 anomaly-detection raw
/// path; LUT-pseudocolor fallback (~5 pp lower image AUROC, arXiv 2108.12505)
/// is taken when this flag is false. Q-E10.
/// </param>
/// <param name="SupportsDualView">
/// True iff the scanner emits a side-view (orthogonal projection) artifact
/// alongside the primary top-down scan, enabling §6.7 dual-view registration.
/// When true, <see cref="DualViewGeometry"/> MUST be non-null. Q-J3.
/// </param>
/// <param name="DualViewGeometry">
/// Physical geometry parameters used to seed the §6.7.4 dual-view calibration
/// defaults — non-null iff <see cref="SupportsDualView"/> is true; null
/// otherwise. See Q-J3.
/// </param>
/// <param name="SupportsDicosExport">
/// True iff the adapter can emit DICOS (NEMA IIC 1) cargo-imaging files in
/// addition to the proprietary vendor format. Design-ready slot per §5.4 /
/// §5.5 — no shipped adapter sets this true on day 1; it's the switch trigger
/// for activating fo-dicom decoding in <see cref="ParseAsync"/>.
/// </param>
/// <param name="DicosFlavors">
/// Which DICOS profile(s) the adapter can emit when
/// <see cref="SupportsDicosExport"/> is true. Empty / null treated as "none".
/// See <see cref="DicosFlavor"/>.
/// </param>
/// <param name="SupportsCalibrationMode">
/// True iff the device exposes a dedicated calibration-mode capture (used by
/// §6.9 threat-library synthesis to record per-class baselines). FS6000
/// supports it natively; other adapters opt in. See §6.9.2.
/// </param>
public sealed record ScannerCapabilities(
    IReadOnlyList<string> SupportedFormats,
    IReadOnlyList<string> SupportedModes,
    bool SupportsLiveStream,
    bool SupportsDualEnergy,
    bool RawChannelsAvailable = false,
    bool SupportsDualView = false,
    DualViewGeometry? DualViewGeometry = null,
    bool SupportsDicosExport = false,
    IReadOnlyList<DicosFlavor>? DicosFlavors = null,
    bool SupportsCalibrationMode = false);

/// <summary>
/// Per-instance config blob the host passes to every adapter call.
/// <para>
/// <c>TenantId</c> is the resolved tenant for this invocation; adapters that
/// keep static / process-wide caches MUST partition them by <c>TenantId</c>
/// so two tenants pointing at the same physical resource don't share state.
/// </para>
/// </summary>
public sealed record ScannerDeviceConfig(
    Guid DeviceId,
    Guid LocationId,
    Guid? StationId,
    long TenantId,
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

/// <summary>
/// Adapter-parsed artifact in a vendor-neutral shape.
/// <para>
/// <see cref="FormatVersion"/> is the §6.9.9 fail-closed guard for adapter
/// firmware drift — when an FS6000 firmware update changes the calibration-
/// mode raw layout, ingest stage 1 fails loud rather than silently passing
/// garbled HE/LE through. Default <c>"unknown"</c> preserves prior behavior
/// for adapters that haven't yet been updated to stamp it.
/// </para>
/// </summary>
public sealed record ParsedArtifact(
    Guid DeviceId,
    DateTimeOffset CapturedAt,
    int WidthPx,
    int HeightPx,
    int Channels,
    string MimeType,
    byte[] Bytes,
    IReadOnlyDictionary<string, string> Metadata,
    string FormatVersion = "unknown");
