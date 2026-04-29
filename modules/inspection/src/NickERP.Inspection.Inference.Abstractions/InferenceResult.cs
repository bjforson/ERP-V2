namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Outcome of a single <see cref="ILoadedModel.RunAsync"/> call. Output
/// tensors are owned by the caller (the runner allocates and the caller
/// disposes once postprocess is finished).
/// </summary>
public sealed class InferenceResult
{
    /// <summary>Output tensors keyed by the model's output port name. Caller disposes once consumed.</summary>
    public required IReadOnlyDictionary<string, ITensor> Outputs { get; init; }

    /// <summary>Per-phase timings + EP + peak alloc for telemetry; emitted as <c>inference.run</c> span attributes.</summary>
    public required InferenceMetrics Metrics { get; init; }
}
