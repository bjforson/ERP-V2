using System.Diagnostics;
using System.Security.Cryptography;
using NickERP.Inspection.Inference.Abstractions;

namespace NickERP.Inspection.Inference.Mock;

/// <summary>
/// <see cref="ILoadedModel"/> for the mock runner. Returns deterministic
/// noise tensors matching <see cref="ModelMetadata.Outputs"/>; the seed is
/// a hash of the input bytes so identical inputs reproduce identical
/// outputs across calls (deterministic and reproducible — fundamental for
/// snapshot tests).
/// <para>
/// Optional <see cref="Fixtures"/> map lets a test pin a specific input
/// hash to a specific output dictionary; if a request's hash is in the
/// fixture map the loaded model returns those tensors verbatim instead
/// of synthesising noise.
/// </para>
/// </summary>
public sealed class MockLoadedModel : ILoadedModel
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITensor>>? _fixtures;
    private int _disposed;

    /// <param name="modelId">Logical model id (echoed verbatim).</param>
    /// <param name="modelVersion">Artifact version (echoed verbatim).</param>
    /// <param name="metadata">Model metadata; <c>RunAsync</c> uses <see cref="ModelMetadata.Outputs"/> to size the synthetic outputs.</param>
    /// <param name="fixtures">Optional input-hash → output-dict map. Hash format: hex-encoded SHA-256 of the concatenated input bytes (in port name order).</param>
    public MockLoadedModel(
        string modelId,
        string modelVersion,
        ModelMetadata metadata,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ITensor>>? fixtures = null)
    {
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        ModelVersion = modelVersion ?? throw new ArgumentNullException(nameof(modelVersion));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _fixtures = fixtures;
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public string ModelVersion { get; }

    /// <inheritdoc />
    public ModelMetadata Metadata { get; }

    /// <inheritdoc />
    public string ExecutionProviderUsed => "Mock";

    /// <inheritdoc />
    public Task<InferenceResult> RunAsync(InferenceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(MockLoadedModel));

        var preStart = Stopwatch.GetTimestamp();
        var inputHash = ComputeInputHash(request);
        var preEnd = Stopwatch.GetTimestamp();

        var runStart = Stopwatch.GetTimestamp();
        Dictionary<string, ITensor> outputs;
        if (_fixtures is not null && _fixtures.TryGetValue(inputHash, out var fixture))
        {
            outputs = new Dictionary<string, ITensor>(fixture.Count);
            foreach (var (k, v) in fixture) outputs[k] = v;
        }
        else
        {
            outputs = SyntheticOutputs(inputHash);
        }
        var runEnd = Stopwatch.GetTimestamp();

        var postStart = Stopwatch.GetTimestamp();
        // No-op postprocess phase — Stopwatch deltas zero out below.
        var postEnd = Stopwatch.GetTimestamp();

        return Task.FromResult(new InferenceResult
        {
            Outputs = outputs,
            Metrics = new InferenceMetrics(
                PreprocessUs: ToMicroseconds(preEnd - preStart),
                InferenceUs: ToMicroseconds(runEnd - runStart),
                PostprocessUs: ToMicroseconds(postEnd - postStart),
                ExecutionProviderUsed: ExecutionProviderUsed,
                PeakBytesAllocated: 0)
        });
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private Dictionary<string, ITensor> SyntheticOutputs(string inputHash)
    {
        // Convert the first 8 hex chars of the hash to a long seed so two
        // calls with identical inputs produce identical outputs.
        var seed = ParseSeed(inputHash);
        var outputs = new Dictionary<string, ITensor>(Metadata.Outputs.Count);
        foreach (var d in Metadata.Outputs)
        {
            // Resolve dynamic dims to 1 for synthetic tensors.
            var shape = new int[d.Shape.Count];
            for (var i = 0; i < d.Shape.Count; i++) shape[i] = d.Shape[i] ?? 1;
            outputs[d.Name] = MockTensor.Random(shape, d.ElementType, seed ^ d.Name.GetHashCode(StringComparison.Ordinal));
        }
        return outputs;
    }

    private static string ComputeInputHash(InferenceRequest request)
    {
        // SHA-256 over (port-name || tensor-bytes) for each input, in port-name order.
        // Stable across calls so identical InferenceRequests collide.
        using var sha = SHA256.Create();
        // Order by name explicitly — the IReadOnlyDictionary contract gives
        // no enumeration order guarantee.
        foreach (var name in request.Inputs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            var tensor = request.Inputs[name];
            // AsBytes returns a Span<byte>; copy out a managed array because
            // SHA256.TransformBlock takes byte[]. Mock path — perf is not the
            // priority; correctness is.
            var src = tensor.AsBytes();
            var copy = src.ToArray();
            sha.TransformBlock(copy, 0, copy.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static long ParseSeed(string hash)
    {
        // First 16 hex chars → 64-bit seed.
        var slice = hash.AsSpan(0, Math.Min(16, hash.Length));
        return long.TryParse(slice, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : 0L;
    }

    private static long ToMicroseconds(long ticks)
    {
        var perMicro = Stopwatch.Frequency / 1_000_000.0;
        return perMicro <= 0 ? 0 : (long)(ticks / perMicro);
    }
}
