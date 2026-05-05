using System.Text.Json;

namespace NickERP.Inspection.Authorities.CustomsGh;

/// <summary>
/// Read-only helpers for picking specific fields out of a BOEScanDocument
/// JSON payload (the shape produced by <c>IcumsGh</c> — and v1's ICUMS
/// ingestion before that). Built around <see cref="JsonElement"/> so we
/// don't pay for a full deserialize when a rule only needs one field.
/// </summary>
internal sealed class BoePayloadReader
{
    private readonly JsonElement _root;

    private BoePayloadReader(JsonElement root) => _root = root;

    public static BoePayloadReader? TryParse(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            // Clone so the JsonDocument can be safely disposed.
            return new BoePayloadReader(doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? DeliveryPlace => ReadString(_root, "ManifestDetails", "DeliveryPlace");
    public string? ClearanceType => ReadString(_root, "Header", "ClearanceType");
    public string? RegimeCode => ReadString(_root, "Header", "RegimeCode");
    public string? DeclarationNumber => ReadString(_root, "Header", "DeclarationNumber");
    public string? ContainerNumber => ReadString(_root, "ContainerDetails", "ContainerNumber");
    public string? CrmsLevel => ReadString(_root, "Header", "CRMSLevel");
    /// <summary>
    /// Sprint 48 / Phase C — half-state CMR port fields. Mirrors the v1
    /// NSCIM ICUMS payload's <c>ManifestDetails.PortOfLoading</c> +
    /// <c>ManifestDetails.PortOfDischarge</c> shape; null when the doc
    /// is not in the half-state phase or when ICUMS pushed the field
    /// blank.
    /// </summary>
    public string? PortOfLoading => ReadString(_root, "ManifestDetails", "PortOfLoading");

    /// <summary>Sprint 48 / Phase C — see <see cref="PortOfLoading"/>.</summary>
    public string? PortOfDischarge => ReadString(_root, "ManifestDetails", "PortOfDischarge");

    private static string? ReadString(JsonElement parent, string p1, string p2)
    {
        if (!parent.TryGetProperty(p1, out var lvl1)) return null;
        if (!lvl1.TryGetProperty(p2, out var lvl2)) return null;
        return lvl2.ValueKind == JsonValueKind.String ? lvl2.GetString() : null;
    }
}
