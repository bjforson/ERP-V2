namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Per-runner configuration supplied by the host (typically read from
/// <c>NickErp:Inspection:Inference:&lt;runner&gt;</c>). Independent of any
/// individual model — those use <see cref="ModelLoadOptions"/>.
/// </summary>
public sealed class InferenceRunnerConfig
{
    /// <summary>
    /// Preferred execution provider, e.g. <c>"DirectML"</c>, <c>"CUDA"</c>,
    /// <c>"TensorRT"</c>, <c>"CPU"</c>. The runner walks
    /// <see cref="FallbackExecutionProviders"/> in order if the preferred
    /// one is unavailable.
    /// </summary>
    public required string PreferredExecutionProvider { get; init; }

    /// <summary>Ordered fallback chain when <see cref="PreferredExecutionProvider"/> is unavailable.</summary>
    public IReadOnlyList<string> FallbackExecutionProviders { get; init; }
        = new[] { "CPU" };

    /// <summary>Intra-op thread count (CPU EP). 0 / 1 leaves the runtime default.</summary>
    public int IntraOpThreads { get; init; } = 1;

    /// <summary>Inter-op thread count (CPU EP). 0 / 1 leaves the runtime default.</summary>
    public int InterOpThreads { get; init; } = 1;

    /// <summary>Optional GPU device ordinal (DirectML / CUDA / TensorRT). Null = runtime default.</summary>
    public int? GpuDeviceId { get; init; }

    /// <summary>Upper bound on warmup-inference duration before the runner gives up and returns the loaded model anyway.</summary>
    public TimeSpan SessionWarmupTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
