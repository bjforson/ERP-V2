namespace NickERP.Inspection.Authorities.CustomsGh;

/// <summary>
/// Sprint 28 — bound configuration for the Customs Ghana validation rule
/// pack. Keeps Ghana-specific data (port codes, regime sets, Fyco patterns)
/// out of the vendor-neutral engine and out of hard-coded constants —
/// operators can override anything via configuration without redeploy.
///
/// <para>
/// Wired in <see cref="Validation.ServiceCollectionExtensions.AddCustomsGhValidation"/>;
/// the configuration section is <c>CustomsGhValidation</c> by convention.
/// </para>
/// </summary>
public sealed class CustomsGhValidationOptions
{
    /// <summary>The conventional configuration section name.</summary>
    public const string SectionName = "CustomsGhValidation";

    /// <summary>
    /// Scanner type code → expected Ghana port code.
    ///
    /// <para>
    /// Defaults baked in: FS6000 sits at the Takoradi sea port (TKD), and
    /// every ASE scanner in the fleet is at Tema (TMA). Per the
    /// <c>reference_port_match_rules_enabled_2026_05_02</c> memory note,
    /// <c>ips1</c>/<c>ips2</c> stations are TKD-side viewing stations
    /// (NOT Tema operators) — the rule maps by scanner TYPE, not by
    /// operator id, so this stays correct.
    /// </para>
    ///
    /// <para>
    /// Operators override per deployment via the
    /// <c>CustomsGhValidation:PortMatchMap</c> section — the configured
    /// dictionary REPLACES (not merges with) the defaults so a new
    /// scanner type can be onboarded without touching code.
    /// </para>
    /// </summary>
    public IDictionary<string, string> PortMatchMap { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FS6000"] = "TKD",
            ["ASE"] = "TMA"
        };

    /// <summary>
    /// Regime codes that mean "this is an import-direction declaration".
    /// Operator-validated set per the 2026-05-02 memory note.
    /// </summary>
    public IList<string> ImportRegimeCodes { get; set; } =
        new List<string> { "40", "50", "61", "62", "70", "90" };

    /// <summary>
    /// Regime codes that mean "this is an export-direction declaration".
    /// </summary>
    public IList<string> ExportRegimeCodes { get; set; } =
        new List<string> { "10", "19", "20", "34", "39" };

    /// <summary>
    /// Regime codes that mean "this is true transit cargo". Per memory:
    /// transit cargo arrives by vessel and leaves Ghana overland — a
    /// fyco=EXPORT scan on transit is a real anomaly the rule must
    /// catch (the 2026-05-04 operator clarification reverted an earlier
    /// "transit-skip" attempt).
    /// </summary>
    public IList<string> TransitRegimeCodes { get; set; } =
        new List<string> { "80", "88", "89" };

    /// <summary>
    /// Regex matching FycoPresent values that mean "this scan is an
    /// export". Operators have observed v1 free-text entries like
    /// <c>WAYBILL/EXPORT</c>, <c>WAYBIL/EXPORT</c>, <c>EPORT</c> in the
    /// FS6000 FycoPresent field; the v1 narrow parser missed these.
    /// Default broadened pattern is case-insensitive and tolerates
    /// typos.
    /// </summary>
    public string FycoExportPattern { get; set; } = @"(?i)\bex(p?)ort\b|^\s*[1Yy]\s*$|^\s*(yes|true)\s*$";

    /// <summary>
    /// Regex matching FycoPresent values that mean "this scan is an
    /// import". Far less data-entry variability in the wild than
    /// export, so the default is conservative.
    /// </summary>
    public string FycoImportPattern { get; set; } = @"(?i)\bimport\b|^\s*[0Nn]\s*$|^\s*(no|false)\s*$";
}
