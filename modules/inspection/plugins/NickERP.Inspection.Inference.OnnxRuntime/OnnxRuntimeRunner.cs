using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using NickERP.Inspection.Inference.Abstractions;
using NickERP.Platform.Plugins;
using ModelMetadata = NickERP.Inspection.Inference.Abstractions.ModelMetadata;
using OrtTensorElementType = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;

namespace NickERP.Inspection.Inference.OnnxRuntime;

/// <summary>
/// Default <see cref="IInferenceRunner"/>. Uses Microsoft.ML.OnnxRuntime
/// with the DirectML and CPU execution providers. Verifies the artifact
/// SHA-256 before opening the session and warms with a metadata-derived
/// dummy input when <see cref="ModelLoadOptions.WarmupOnLoad"/> is set.
/// </summary>
[Plugin("onnx-runtime")]
public sealed class OnnxRuntimeRunner : IInferenceRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OnnxRuntimeRunner> _logger;
    private readonly InferenceRunnerConfig? _defaultConfig;

    /// <summary>Default ctor — used by the plugin loader (no logging, no thread budget).</summary>
    public OnnxRuntimeRunner() : this(NullLoggerFactory.Instance, defaultConfig: null) { }

    /// <summary>DI ctor without runner-level config; ORT picks its own thread defaults.</summary>
    public OnnxRuntimeRunner(ILoggerFactory loggerFactory) : this(loggerFactory, defaultConfig: null) { }

    /// <summary>
    /// DI ctor with runner-level <see cref="InferenceRunnerConfig"/> — used when the host registers
    /// the runner via <see cref="ServiceCollectionExtensions.AddInferenceOnnxRuntime"/> and supplies
    /// a thread-budget config. <c>IntraOpThreads</c> / <c>InterOpThreads</c> from the supplied config
    /// are applied to every session opened by <see cref="LoadAsync"/>.
    /// </summary>
    public OnnxRuntimeRunner(ILoggerFactory loggerFactory, InferenceRunnerConfig? defaultConfig)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = _loggerFactory.CreateLogger<OnnxRuntimeRunner>();
        _defaultConfig = defaultConfig;
    }

    /// <inheritdoc />
    public string TypeCode => "onnx-runtime";

    /// <inheritdoc />
    public InferenceRunnerCapabilities Capabilities { get; } = new(
        SupportsBatch: true,
        SupportsDynamicShapes: true,
        SupportsFp16: true,
        SupportsInt8: true,
        MaxModelSizeBytes: 0,
        AvailableExecutionProviders: DetectAvailableExecutionProviders());

    /// <inheritdoc />
    public Task<ConnectionTestResult> TestAsync(InferenceRunnerConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        var available = Capabilities.AvailableExecutionProviders;
        var preferred = config.PreferredExecutionProvider;

        if (!available.Contains(preferred, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ConnectionTestResult(
                Success: false,
                Message: $"Preferred EP '{preferred}' is not available. Detected: {string.Join(", ", available)}.",
                Diagnostics: new Dictionary<string, string>
                {
                    ["ep.preferred"] = preferred,
                    ["ep.available"] = string.Join(",", available)
                }));
        }

        return Task.FromResult(new ConnectionTestResult(
            Success: true,
            Message: $"OnnxRuntime ready. Preferred EP '{preferred}' is available.",
            Diagnostics: new Dictionary<string, string>
            {
                ["ep.preferred"] = preferred,
                ["ep.available"] = string.Join(",", available),
                ["onnxruntime.version"] = OrtEnv.Instance().GetVersionString()
            }));
    }

    /// <inheritdoc />
    public async Task<ILoadedModel> LoadAsync(
        ModelArtifact artifact,
        ModelLoadOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(artifact.Path))
        {
            throw new FileNotFoundException(
                $"Model artifact not found: {artifact.Path} ({artifact.ModelId} {artifact.Version}).",
                artifact.Path);
        }

        // 1. SHA-256 fail-fast.
        var actualSha = await ComputeSha256Async(artifact.Path, ct).ConfigureAwait(false);
        if (!string.Equals(actualSha, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Model artifact sha256 mismatch for {artifact.ModelId} {artifact.Version}. " +
                $"Expected {artifact.Sha256}, got {actualSha}. Path: {artifact.Path}.");
        }

        // 2. Build SessionOptions with the requested EP chain + runner-level thread budget.
        var preferred = options.PreferredExecutionProvider ?? "CPU";
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        ApplyThreadBudget(sessionOptions);

        var resolvedEp = AppendExecutionProvider(sessionOptions, preferred, options.GpuDeviceId);
        InferenceSession session;
        try
        {
            session = new InferenceSession(artifact.Path, sessionOptions);
        }
        catch (Exception primaryEx)
        {
            _logger.LogWarning(primaryEx,
                "OnnxRuntimeRunner: failed to load {ModelId} {Version} on EP '{Ep}'; falling back to CPU.",
                artifact.ModelId, artifact.Version, resolvedEp);

            // Cleanup and retry on CPU as the universal fallback.
            sessionOptions.Dispose();
            sessionOptions = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            ApplyThreadBudget(sessionOptions);
            resolvedEp = "CPU";
            session = new InferenceSession(artifact.Path, sessionOptions);
        }

        // 3. Extract metadata from the open session.
        var metadata = BuildMetadata(session);

        var loaded = new OnnxLoadedModel(
            session,
            artifact.ModelId,
            artifact.Version,
            metadata,
            resolvedEp,
            _loggerFactory.CreateLogger<OnnxLoadedModel>());

        // 4. Optional warmup. Failure here is a warning, not a load failure —
        // first real request will surface a hard failure if the session is
        // genuinely broken.
        if (options.WarmupOnLoad)
        {
            try
            {
                await WarmupAsync(loaded, metadata, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OnnxRuntimeRunner: warmup failed for {ModelId} {Version} on EP '{Ep}'; continuing without warmup.",
                    artifact.ModelId, artifact.Version, resolvedEp);
            }
        }

        return loaded;
    }

    // --- helpers --------------------------------------------------------

    /// <summary>
    /// Apply <see cref="InferenceRunnerConfig.IntraOpThreads"/> / <see cref="InferenceRunnerConfig.InterOpThreads"/>
    /// from the runner-level config (if any) to the per-session <see cref="SessionOptions"/>.
    /// No-op when no config is registered — ORT picks its own auto-detected defaults.
    /// </summary>
    private void ApplyThreadBudget(SessionOptions sessionOptions)
    {
        if (_defaultConfig is null) return;
        sessionOptions.IntraOpNumThreads = _defaultConfig.IntraOpThreads;
        sessionOptions.InterOpNumThreads = _defaultConfig.InterOpThreads;
    }

    /// <summary>Discover EPs once at type init. Re-querying mid-process is wasteful and the answer doesn't change.</summary>
    private static IReadOnlyList<string> DetectAvailableExecutionProviders()
    {
        try
        {
            var providers = OrtEnv.Instance().GetAvailableProviders();
            // Normalize provider names to the short labels the rest of the
            // system (admin UI, plugin.json) uses. ORT returns names like
            // "DmlExecutionProvider", "CPUExecutionProvider".
            var normalized = new List<string>(providers.Length);
            foreach (var raw in providers)
            {
                normalized.Add(NormalizeProviderName(raw));
            }
            return normalized;
        }
        catch
        {
            // OrtEnv access can fail in test contexts where the native
            // runtime isn't on the search path. Surface a CPU-only set so
            // tests don't crash at type init.
            return new[] { "CPU" };
        }
    }

    private static string NormalizeProviderName(string raw) => raw switch
    {
        "DmlExecutionProvider" => "DirectML",
        "CPUExecutionProvider" => "CPU",
        "CUDAExecutionProvider" => "CUDA",
        "TensorrtExecutionProvider" => "TensorRT",
        _ => raw
    };

    private static string AppendExecutionProvider(SessionOptions options, string preferred, int? gpuDeviceId)
    {
        switch (preferred.ToUpperInvariant())
        {
            case "DIRECTML":
            case "DML":
                options.AppendExecutionProvider_DML(gpuDeviceId ?? 0);
                return "DirectML";

            case "CPU":
                // CPU EP is implicit / always available; nothing to append.
                return "CPU";

            default:
                // Unknown EP name; fall through to CPU. Logged at the call site.
                return "CPU";
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static ModelMetadata BuildMetadata(InferenceSession session)
    {
        var inputs = new List<TensorDescriptor>(session.InputMetadata.Count);
        foreach (var (name, meta) in session.InputMetadata)
        {
            inputs.Add(BuildDescriptor(name, meta));
        }

        var outputs = new List<TensorDescriptor>(session.OutputMetadata.Count);
        foreach (var (name, meta) in session.OutputMetadata)
        {
            outputs.Add(BuildDescriptor(name, meta));
        }

        // Embedded model metadata (producer, opset, custom_metadata_map) lives
        // on session.ModelMetadata. Opset isn't directly exposed as a single
        // string; surface the GraphDescription / Domain / ProducerName instead.
        Dictionary<string, string>? custom = null;
        var modelMeta = session.ModelMetadata;
        if (modelMeta.CustomMetadataMap is { Count: > 0 } map)
        {
            custom = new Dictionary<string, string>(map.Count);
            foreach (var kv in map)
            {
                custom[kv.Key] = kv.Value;
            }
        }

        return new ModelMetadata
        {
            Inputs = inputs,
            Outputs = outputs,
            // Best-effort opset string until ORT surfaces it as a typed value.
            OnnxOpset = !string.IsNullOrWhiteSpace(modelMeta.GraphDescription)
                ? modelMeta.GraphDescription
                : (!string.IsNullOrWhiteSpace(modelMeta.ProducerName) ? modelMeta.ProducerName : "unknown"),
            CustomMetadata = custom
        };
    }

    private static TensorDescriptor BuildDescriptor(string name, NodeMetadata meta)
    {
        var element = OnnxTensor.MapElementType(meta.ElementDataType);
        var dims = meta.Dimensions;
        var shape = new int?[dims.Length];
        for (var i = 0; i < dims.Length; i++)
        {
            // ORT uses -1 for symbolic / dynamic dimensions.
            shape[i] = dims[i] < 0 ? null : dims[i];
        }
        return new TensorDescriptor(name, element, shape);
    }

    /// <summary>One synthetic <c>session.Run</c> using metadata-derived dummy inputs.</summary>
    private static async Task WarmupAsync(OnnxLoadedModel loaded, ModelMetadata metadata, CancellationToken ct)
    {
        var inputs = new Dictionary<string, ITensor>(metadata.Inputs.Count);
        try
        {
            foreach (var d in metadata.Inputs)
            {
                inputs[d.Name] = SyntheticInputTensor(d);
            }
            var req = new InferenceRequest
            {
                Inputs = inputs,
                CorrelationId = $"warmup:{loaded.ModelId}:{loaded.ModelVersion}",
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await loaded.RunAsync(req, cts.Token).ConfigureAwait(false);
            foreach (var (_, t) in result.Outputs) t.Dispose();
        }
        finally
        {
            foreach (var (_, t) in inputs) t.Dispose();
        }
    }

    /// <summary>Build a zero-filled tensor matching the declared input descriptor; dynamic dims default to 1.</summary>
    private static ITensor SyntheticInputTensor(TensorDescriptor descriptor)
    {
        var shapeLong = new long[descriptor.Shape.Count];
        for (var i = 0; i < descriptor.Shape.Count; i++)
        {
            shapeLong[i] = descriptor.Shape[i] ?? 1L;
        }

        // Allocate a CPU-side OrtValue of the right type and shape; bytes
        // are zero-initialized which is fine for a smoke-test forward pass.
        var ortType = OnnxTensor.MapElementType(descriptor.ElementType);
        var value = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance,
            ortType,
            shapeLong);
        return new OnnxTensor(value);
    }
}
