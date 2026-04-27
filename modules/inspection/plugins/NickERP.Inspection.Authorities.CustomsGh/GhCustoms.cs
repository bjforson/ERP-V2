namespace NickERP.Inspection.Authorities.CustomsGh;

/// <summary>
/// Ghana customs constants, ported from v1 (NSCIM ContainerValidationService
/// + IcumDownloadsModels). Single point of truth so individual rules don't
/// hard-code magic strings.
/// </summary>
internal static class GhCustoms
{
    /// <summary>Port codes that appear inside <c>BOE.DeliveryPlace</c> at offset 2..5 (e.g. <c>WTTMA1MPS3</c>).</summary>
    public static class Ports
    {
        public const string Tema = "TMA";
        public const string Takoradi = "TKD";
        public const string Kotoka = "KIA";
        public const string Aflao = "AFL";
        public const string Elubo = "ELU";

        public static readonly IReadOnlySet<string> All =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Tema, Takoradi, Kotoka, Aflao, Elubo };
    }

    /// <summary>
    /// Mapping from a v2 location code → the upstream BOE port code Ghana
    /// Customs uses. Locations are configured by the operator (per-deployment),
    /// so the keys here are the conventional codes from the original vision
    /// (Tema first, Kotoka next per ROADMAP §4.5). Unknown locations skip
    /// the port-match rule rather than fail it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LocationToPort =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tema"] = Ports.Tema,
            ["takoradi"] = Ports.Takoradi,
            ["kotoka"] = Ports.Kotoka,
            ["kia"] = Ports.Kotoka,
            ["aflao"] = Ports.Aflao,
            ["elubo"] = Ports.Elubo,
        };

    /// <summary>BOE clearance types we recognize. <c>CMR</c> is pre-BOE (transit, no direction yet).</summary>
    public static class ClearanceTypes
    {
        public const string Import = "IM";
        public const string Export = "EX";
        public const string Transit = "CMR";
    }

    /// <summary>
    /// Ghana Customs regime code reference. Codes outside this set are not
    /// rejected outright — they trigger an informational violation only,
    /// because new regimes get added without notice.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RegimeCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["40"] = "Direct import for home consumption",
            ["41"] = "Conditional release pending duty",
            ["50"] = "Temporary import",
            ["60"] = "Re-importation",
            ["70"] = "Warehousing",
            ["71"] = "Customs warehouse entry",
            ["72"] = "Bonded warehouse transfer",
            ["80"] = "Transit / CMR (per memory note: regime 80 stays CMR)",
            ["90"] = "Re-export",
            ["91"] = "Export drawback",
        };

    /// <summary>Severity vocabulary aligned with v1 result categories.</summary>
    public static class Severity
    {
        public const string Info = "Info";
        public const string Warning = "Warning";
        public const string Error = "Error";
    }

    /// <summary>Stable rule codes for downstream UI / analytics.</summary>
    public static class RuleCodes
    {
        public const string PortMatch = "GH-PORT-MATCH";
        public const string FycoCheck = "GH-FYCO";
        public const string RegimeCheck = "GH-REGIME";
        public const string CmrUpgrade = "GH-CMR-UPGRADE";
    }

    /// <summary>Mutation kinds emitted from <see cref="IAuthorityRulesProvider.InferAsync"/>.</summary>
    public static class MutationKinds
    {
        public const string PromoteCmrToIm = "promote_cmr_to_im";
    }

    /// <summary>
    /// Pull the 3-character port code from a BOE DeliveryPlace string. Returns
    /// null when the format isn't recognized — v1 saw a ~0.35% rate of malformed
    /// DeliveryPlace values and elected to skip the rule rather than fail.
    /// </summary>
    public static string? ExtractPortCode(string? deliveryPlace)
    {
        if (string.IsNullOrEmpty(deliveryPlace) || deliveryPlace.Length < 5) return null;
        var code = deliveryPlace.Substring(2, 3).ToUpperInvariant();
        return Ports.All.Contains(code) ? code : null;
    }
}
