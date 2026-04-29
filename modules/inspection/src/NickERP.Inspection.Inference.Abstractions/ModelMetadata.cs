namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Static metadata observed by the runner at <see cref="IInferenceRunner.LoadAsync"/>
/// time — input/output ports, opset, free-form custom metadata embedded
/// in the artifact (e.g. <c>"label_map"</c>, <c>"trained_on"</c>). The
/// host uses <see cref="Inputs"/> to derive a synthetic warmup tensor
/// when <see cref="ModelLoadOptions.WarmupOnLoad"/> is set.
/// </summary>
public sealed class ModelMetadata
{
    /// <summary>Declared input ports.</summary>
    public required IReadOnlyList<TensorDescriptor> Inputs { get; init; }

    /// <summary>Declared output ports.</summary>
    public required IReadOnlyList<TensorDescriptor> Outputs { get; init; }

    /// <summary>ONNX opset (e.g. <c>"17"</c>) or runtime-equivalent version tag.</summary>
    public required string OnnxOpset { get; init; }

    /// <summary>Optional free-form metadata embedded in the artifact (label map, training notes, etc.).</summary>
    public IReadOnlyDictionary<string, string>? CustomMetadata { get; init; }
}
