namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Hot handle to a loaded model. One singleton per active
/// <c>(model_id, version)</c>, held by DI. <see cref="DisposeAsync"/>
/// releases the underlying session/native handles after a 30-second drain
/// (handled by the host, not the runner — the runner just needs idempotent
/// disposal).
/// </summary>
public interface ILoadedModel : IAsyncDisposable
{
    /// <summary>Logical model id, e.g. <c>container-split</c>.</summary>
    string ModelId { get; }

    /// <summary>SemVer of the loaded artifact, e.g. <c>v3.1.0</c>.</summary>
    string ModelVersion { get; }

    /// <summary>Inputs/outputs/opset/custom metadata as observed by the runner at load time.</summary>
    ModelMetadata Metadata { get; }

    /// <summary>Execution provider actually resolved at load time (e.g. <c>DirectML</c>, <c>CPU</c>). Telemetry attribute.</summary>
    string ExecutionProviderUsed { get; }

    /// <summary>
    /// Execute inference on this loaded model. The runner is responsible
    /// for capturing preprocess/run/postprocess timings into
    /// <see cref="InferenceResult.Metrics"/> using a monotonic clock.
    /// In-flight requests SHOULD complete even after the host begins a
    /// hot-swap drain.
    /// </summary>
    Task<InferenceResult> RunAsync(
        InferenceRequest request,
        CancellationToken ct);
}
