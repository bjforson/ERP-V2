namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// One model artifact on disk, together with the metadata the host needs
/// to load it. The runner is required to verify <see cref="Sha256"/>
/// against the on-disk bytes before opening the session.
/// </summary>
public sealed class ModelArtifact
{
    /// <summary>Logical model id. Stable across versions, e.g. <c>"container-split"</c>.</summary>
    public required string ModelId { get; init; }

    /// <summary>Artifact version (SemVer recommended), e.g. <c>"v3.1.0"</c>.</summary>
    public required string Version { get; init; }

    /// <summary>Canonical disk path of the artifact (the runner reads it as-is).</summary>
    public required string Path { get; init; }

    /// <summary>Hex-encoded SHA-256 of the artifact bytes; the runner SHOULD fail-fast on mismatch.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Optional free-form tags (e.g. <c>"family"</c>, <c>"trained_on"</c>) surfaced in admin UI and telemetry.</summary>
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
}
