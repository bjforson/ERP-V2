using NickERP.Inspection.Inference.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.Inference.Mock;

/// <summary>
/// Synthetic <see cref="IInferenceRunner"/> for end-to-end pipeline tests.
/// Reports unrestricted capabilities and returns deterministic noise from
/// <see cref="MockLoadedModel"/>; sha256 verification is skipped because
/// the artifact need not exist on disk for tests that just exercise the
/// load/run path. Use <see cref="WithFixtures"/> for snapshot tests that
/// pin specific inputs to specific outputs.
/// </summary>
[Plugin("mock")]
public sealed class MockInferenceRunner : IInferenceRunner
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITensor>>? _fixtures;

    /// <summary>Default ctor — no fixtures, fully synthetic.</summary>
    public MockInferenceRunner() : this(fixtures: null) { }

    /// <summary>Ctor with fixtures (input-hash → output-dict map). See <see cref="MockLoadedModel"/> for the hash format.</summary>
    public MockInferenceRunner(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITensor>>? fixtures)
    {
        _fixtures = fixtures;
    }

    /// <inheritdoc />
    public string TypeCode => "mock";

    /// <inheritdoc />
    public InferenceRunnerCapabilities Capabilities { get; } = new(
        SupportsBatch: true,
        SupportsDynamicShapes: true,
        SupportsFp16: true,
        SupportsInt8: true,
        MaxModelSizeBytes: 0,
        AvailableExecutionProviders: new[] { "Mock" });

    /// <inheritdoc />
    public Task<ConnectionTestResult> TestAsync(InferenceRunnerConfig config, CancellationToken ct)
    {
        return Task.FromResult(new ConnectionTestResult(
            Success: true,
            Message: "Mock inference runner — always reachable.",
            Diagnostics: new Dictionary<string, string> { ["ep.used"] = "Mock" }));
    }

    /// <inheritdoc />
    public Task<ILoadedModel> LoadAsync(ModelArtifact artifact, ModelLoadOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);

        // Mock runner skips sha256 verification — tests usually don't have
        // a real artifact on disk. If callers pass a real path we leave it
        // alone; if not, we synthesise minimal metadata so RunAsync has
        // shapes to work with.
        var metadata = SynthesiseMetadata(artifact);
        ILoadedModel loaded = new MockLoadedModel(artifact.ModelId, artifact.Version, metadata, _fixtures);
        return Task.FromResult(loaded);
    }

    /// <summary>
    /// Build a minimal <see cref="ModelMetadata"/> for the mock — one
    /// dynamic-batch float32 input port and one dynamic-batch float32
    /// output port. Tests that need richer shapes can subclass or pass a
    /// fixture instead.
    /// </summary>
    private static ModelMetadata SynthesiseMetadata(ModelArtifact artifact)
    {
        return new ModelMetadata
        {
            Inputs = new[]
            {
                new TensorDescriptor("input", TensorElementType.Float32, new int?[] { null, 3, 224, 224 })
            },
            Outputs = new[]
            {
                new TensorDescriptor("output", TensorElementType.Float32, new int?[] { null, 1000 })
            },
            OnnxOpset = "mock",
            CustomMetadata = artifact.Tags
        };
    }

    /// <summary>Convenience builder for fixture-backed runners used in tests.</summary>
    public static MockInferenceRunner WithFixtures(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITensor>> fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        return new MockInferenceRunner(fixtures);
    }
}
