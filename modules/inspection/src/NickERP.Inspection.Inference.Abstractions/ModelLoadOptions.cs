namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Per-model load-time options. Overrides the runner's default EP and
/// quantization choices for this artifact only. Lives next to
/// <see cref="ModelArtifact"/> so the host can promote a model with
/// model-specific tuning without re-binding the whole runner config.
/// </summary>
/// <param name="PreferredExecutionProvider">Override the runner-level preferred EP for this model. Null = use runner config.</param>
/// <param name="GpuDeviceId">Override the runner-level GPU ordinal for this model. Null = use runner config.</param>
/// <param name="UseInt8">Hint that the artifact is int8-quantized; runner can pick a more efficient kernel set when supported.</param>
/// <param name="WarmupOnLoad">If true, the runner runs one synthetic inference before returning so the first real request avoids the cold-load tax.</param>
public sealed record ModelLoadOptions(
    string? PreferredExecutionProvider = null,
    int? GpuDeviceId = null,
    bool UseInt8 = false,
    bool WarmupOnLoad = true);
