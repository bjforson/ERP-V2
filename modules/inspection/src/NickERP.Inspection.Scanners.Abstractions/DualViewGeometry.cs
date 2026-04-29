namespace NickERP.Inspection.Scanners.Abstractions;

/// <summary>
/// Physical geometry of a dual-view scanner — surfaced through
/// <see cref="ScannerCapabilities.DualViewGeometry"/> so the §6.7.4
/// calibration defaults (search window, expected belt-coordinate offset)
/// can be seeded automatically at commissioning.
/// <para>
/// Units are explicit in the field names so a misread can't silently drift
/// orders of magnitude. See IMAGE-ANALYSIS-MODERNIZATION.md §6.7 (dual-view
/// registration spec) and Q-J3.
/// </para>
/// </summary>
/// <param name="DetectorSpacingMm">
/// Distance between the top and side detectors along the belt direction,
/// in millimeters. Positive — the side detector is downstream of the top
/// detector (object reaches the top view first).
/// </param>
/// <param name="PixelPitchMmPerPx">
/// Spatial resolution along the belt direction, in millimeters per pixel.
/// Combined with belt speed determines how many pixels of vertical shift
/// correspond to <see cref="DetectorSpacingMm"/>.
/// </param>
/// <param name="NominalBeltSpeedMps">
/// Nominal belt speed in meters per second. Used to derive the §6.7.4
/// default <c>dualview.expected_offset_px</c>. Real per-scan speed may
/// drift; this is the calibration anchor.
/// </param>
public sealed record DualViewGeometry(
    decimal DetectorSpacingMm,
    decimal PixelPitchMmPerPx,
    decimal NominalBeltSpeedMps);

/// <summary>
/// DICOS (NEMA IIC 1) profile flavors a scanner adapter can declare
/// support for via <see cref="ScannerCapabilities.DicosFlavors"/>.
/// See IMAGE-ANALYSIS-MODERNIZATION.md §5.4.
/// </summary>
public enum DicosFlavor
{
    /// <summary>2D cargo radiograph (single projection, single or dual energy).</summary>
    Cargo2D,

    /// <summary>Cargo CT (volumetric tomographic reconstruction).</summary>
    CargoCT,

    /// <summary>
    /// Threat Detection Report — DICOS overlay carrying algorithm-derived
    /// threat regions, confidences, and overall assessment. Maps to v2
    /// <c>Finding</c> + <c>Verdict</c> entities.
    /// </summary>
    TDR,

    /// <summary>
    /// Automated Threat Recognition payload — algorithm-side overlay
    /// distinct from analyst-authored TDR.
    /// </summary>
    ATR,
}
