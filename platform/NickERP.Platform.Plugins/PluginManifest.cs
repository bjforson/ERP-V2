using System.Text.Json;
using System.Text.Json.Serialization;

namespace NickERP.Platform.Plugins;

/// <summary>
/// Sibling <c>plugin.json</c> file shipped next to every plugin assembly.
/// Carries everything the loader and admin UI need to know about the
/// plugin without instantiating it.
/// </summary>
/// <param name="TypeCode">Stable identifier — must match the <see cref="PluginAttribute.TypeCode"/> on the concrete class.</param>
/// <param name="DisplayName">Human-readable label shown in admin UIs.</param>
/// <param name="Version">SemVer of the plugin assembly.</param>
/// <param name="Description">Free-text description.</param>
/// <param name="Contracts">Fully-qualified interface names this plugin implements (e.g. <c>NickERP.Inspection.Scanners.Abstractions.IScannerAdapter</c>). The loader uses this to filter plugins by contract.</param>
/// <param name="ConfigSchema">Optional JSON Schema (raw <c>JsonElement</c>) for instance-level configuration. Admin UI uses it to render config forms; the loader doesn't enforce it in v0.</param>
public sealed record PluginManifest(
    string TypeCode,
    string DisplayName,
    string Version,
    string? Description,
    IReadOnlyList<string> Contracts,
    JsonElement? ConfigSchema)
{
    /// <summary>
    /// Minimum host Abstractions assembly contract version required by this
    /// plugin. Format: <c>"major.minor"</c> (e.g. <c>"1.0"</c>). Required for
    /// any plugin that depends on a non-1.0 contract; left null (or omitted)
    /// for plugins that are fine with any host version. The loader rejects
    /// the plugin at startup when the host's <see cref="ContractVersionAttribute"/>
    /// is older than this value.
    /// </summary>
    [JsonPropertyName("minHostContractVersion")]
    public string? MinHostContractVersion { get; init; }

    /// <summary>The well-known sidecar filename next to each plugin DLL.</summary>
    public const string FileName = "plugin.json";

    /// <summary>JsonSerializer settings used when reading/writing manifests.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Read a manifest from disk. Throws on malformed JSON or missing required fields.</summary>
    public static PluginManifest LoadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Plugin manifest not found: {path}", path);
        }
        var text = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(text, JsonOptions)
            ?? throw new InvalidDataException($"Plugin manifest at {path} deserialised to null.");

        if (string.IsNullOrWhiteSpace(manifest.TypeCode)) throw new InvalidDataException($"{path}: TypeCode is required.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName)) throw new InvalidDataException($"{path}: DisplayName is required.");
        if (string.IsNullOrWhiteSpace(manifest.Version)) throw new InvalidDataException($"{path}: Version is required.");

        return manifest;
    }
}
