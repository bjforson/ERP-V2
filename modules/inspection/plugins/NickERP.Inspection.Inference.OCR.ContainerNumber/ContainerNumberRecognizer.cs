using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using NickERP.Inspection.Inference.Abstractions;
using NickERP.Inspection.Inference.OnnxRuntime;
using NickERP.Platform.Plugins;
using OrtTensorElementType = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;

namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// Default <see cref="IContainerNumberRecognizer"/>. Owns one cached
/// <see cref="ILoadedModel"/> for the configured
/// <c>(ModelId, ModelVersion)</c>; loaded lazily on first request and
/// reused for all subsequent calls. Plugin loader discovery uses the
/// <c>[Plugin("container-ocr-florence2")]</c> attribute.
/// </summary>
[Plugin("container-ocr-florence2")]
public sealed class ContainerNumberRecognizer : IContainerNumberRecognizer, IAsyncDisposable
{
    private readonly IInferenceRunner _runner;
    private readonly Func<ModelArtifact> _artifactProvider;
    private readonly ContainerOcrConfig _config;
    private readonly ILogger<ContainerNumberRecognizer> _logger;

    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private ILoadedModel? _model;
    private int _disposed;

    /// <summary>
    /// Construct the recogniser. The runner is delegated to (typically the
    /// <c>onnx-runtime</c> keyed singleton); the artifact provider is a
    /// callable returning the resolved <see cref="ModelArtifact"/> for the
    /// configured model id+version. Hosts compose the artifact provider from
    /// their model registry; for tests, supply a fixed lambda.
    /// </summary>
    public ContainerNumberRecognizer(
        IInferenceRunner runner,
        Func<ModelArtifact> artifactProvider,
        ContainerOcrConfig config,
        ILogger<ContainerNumberRecognizer>? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _artifactProvider = artifactProvider ?? throw new ArgumentNullException(nameof(artifactProvider));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? NullLogger<ContainerNumberRecognizer>.Instance;
    }

    /// <inheritdoc />
    public async Task<ContainerNumberRecognition> RecognizeAsync(
        ReadOnlyMemory<byte> plateRoiBytes,
        string correlationId,
        int? tenantId,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(ContainerNumberRecognizer));
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("CorrelationId must be non-empty.", nameof(correlationId));
        }

        var sw = Stopwatch.StartNew();
        var loaded = await EnsureLoadedAsync(ct).ConfigureAwait(false);

        // 1. Preprocess into CHW float32. The recogniser owns the buffer so
        // we can copy into the OrtValue without surfacing a span over a
        // disposed pinned region.
        var pre = PlateRoiPreprocessor.Preprocess(
            plateRoiBytes.Span,
            _config.InputHeightPx,
            _config.InputWidthPx);

        // 2. Build a (1, 3, H, W) float32 OnnxTensor. Florence-2's vision
        // tower expects this canonical NCHW layout (§6.1.2).
        var inputName = ResolveImageInputName(loaded);
        var shape = new long[] { 1, 3, _config.InputHeightPx, _config.InputWidthPx };
        var ortValue = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance,
            OrtTensorElementType.Float,
            shape);
        ITensor inputTensor = new OnnxTensor(ortValue);
        try
        {
            var span = inputTensor.AsSpan<float>();
            pre.AsSpan().CopyTo(span);

            var request = new InferenceRequest
            {
                Inputs = new Dictionary<string, ITensor> { [inputName] = inputTensor },
                CorrelationId = correlationId,
                TenantId = tenantId,
                Timeout = null,
            };

            var result = await loaded.RunAsync(request, ct).ConfigureAwait(false);
            try
            {
                return PostProcess(result, loaded, sw.Elapsed);
            }
            finally
            {
                foreach (var (_, t) in result.Outputs) t.Dispose();
            }
        }
        finally
        {
            inputTensor.Dispose();
        }
    }

    private async Task<ILoadedModel> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_model is not null) return _model;
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_model is not null) return _model;

            var artifact = _artifactProvider()
                ?? throw new InvalidOperationException(
                    "Artifact provider returned null. Configure the host's model registry to expose " +
                    $"({_config.ModelId}, {_config.ModelVersion}) before recognise calls.");

            var loadOptions = new ModelLoadOptions(
                PreferredExecutionProvider: null, // use runner default
                GpuDeviceId: null,
                UseInt8: false,
                WarmupOnLoad: true);

            _logger.LogInformation(
                "Container OCR: loading {ModelId} {Version} from {Path} (sha256 {Sha}).",
                artifact.ModelId, artifact.Version, artifact.Path, artifact.Sha256);

            _model = await _runner.LoadAsync(artifact, loadOptions, ct).ConfigureAwait(false);
            return _model;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Pick the model's image-input port. Florence-2 ONNX exports name it
    /// <c>pixel_values</c>; some Donut exports use <c>image</c>. Fall back
    /// to the first input descriptor if neither name is present.
    /// </summary>
    private static string ResolveImageInputName(ILoadedModel loaded)
    {
        foreach (var d in loaded.Metadata.Inputs)
        {
            if (string.Equals(d.Name, "pixel_values", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Name, "image", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Name, "input", StringComparison.OrdinalIgnoreCase))
            {
                return d.Name;
            }
        }
        if (loaded.Metadata.Inputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"Loaded model {loaded.ModelId} {loaded.ModelVersion} reports zero input ports.");
        }
        return loaded.Metadata.Inputs[0].Name;
    }

    /// <summary>
    /// Map the runner's logits output into a decoded ISO 6346 candidate, then
    /// gate by check digit + confidence. The recogniser is forgiving about
    /// the output port name — Florence-2 calls it <c>logits</c>; some ONNX
    /// exports just <c>output_0</c>. We pick the first float32 output.
    /// </summary>
    private ContainerNumberRecognition PostProcess(
        InferenceResult result,
        ILoadedModel loaded,
        TimeSpan elapsed)
    {
        ITensor? logitsTensor = null;
        foreach (var (_, t) in result.Outputs)
        {
            if (t.ElementType == TensorElementType.Float32)
            {
                logitsTensor = t;
                break;
            }
        }
        if (logitsTensor is null)
        {
            _logger.LogWarning(
                "Container OCR: model {ModelId} {Version} produced no float32 outputs; returning manual-queue.",
                loaded.ModelId, loaded.ModelVersion);
            return BuildManualQueueResponse(loaded);
        }

        // Logits arrive as either (1, T, V) (with batch) or (T, V) (no batch).
        // We need to slice down to the 36-symbol alphabet — the model's
        // raw vocabulary V_full is much larger (Florence-2 is BART-style,
        // V_full ≈ 51 200). For first ship we operate against an *already
        // sliced* logits tensor (shape T × 36) carried as a separate output
        // port from the trainer's ONNX export. If the export hasn't been
        // sliced (V_full != 36 and != Iso6346.AllowedAlphabet.Length), we
        // surface manual-queue rather than emit garbage.
        var shape = logitsTensor.Shape;
        if (shape.Count < 2)
        {
            _logger.LogWarning(
                "Container OCR: logits tensor has rank < 2 (shape={Shape}); manual-queue.",
                string.Join(",", shape));
            return BuildManualQueueResponse(loaded);
        }

        int timeSteps = shape.Count >= 3 ? shape[shape.Count - 2] : shape[0];
        int vocabSize = shape[shape.Count - 1];

        if (vocabSize != Iso6346.AllowedAlphabet.Length)
        {
            _logger.LogWarning(
                "Container OCR: logits vocab dim={Vocab} != 36 (the ISO 6346 alphabet). " +
                "ONNX export must include a final slice/index that projects to the 36-symbol grammar; " +
                "manual-queue.",
                vocabSize);
            return BuildManualQueueResponse(loaded);
        }

        var logits = logitsTensor.AsSpan<float>();

        var (decoded, conf) = ConstrainedBeamDecoder.Decode(
            logits,
            timeSteps: timeSteps,
            vocabSize: vocabSize,
            beamWidth: _config.BeamWidth,
            maxBudget: _config.MaxTokenBudget);

        if (decoded == Iso6346.UnreadableSentinel)
        {
            return new ContainerNumberRecognition(
                Predicted: Iso6346.UnreadableSentinel,
                Confidence: 0.0,
                CheckDigitPassed: false,
                DecodePath: ContainerOcrDecodePath.ManualQueueRequired,
                ModelId: loaded.ModelId,
                ModelVersion: loaded.ModelVersion);
        }

        var checkDigitOk = Iso6346.IsValid(decoded);
        var path = (checkDigitOk && conf >= _config.ConfidenceGate)
            ? ContainerOcrDecodePath.Primary
            : ContainerOcrDecodePath.ManualQueueRequired;

        _logger.LogInformation(
            "Container OCR: decoded={Decoded} conf={Conf:F3} checkDigit={Check} path={Path} elapsed={ElapsedMs}ms",
            decoded, conf, checkDigitOk, path, (long)elapsed.TotalMilliseconds);

        return new ContainerNumberRecognition(
            Predicted: decoded,
            Confidence: conf,
            CheckDigitPassed: checkDigitOk,
            DecodePath: path,
            ModelId: loaded.ModelId,
            ModelVersion: loaded.ModelVersion);
    }

    private ContainerNumberRecognition BuildManualQueueResponse(ILoadedModel loaded) =>
        new(
            Predicted: Iso6346.UnreadableSentinel,
            Confidence: 0.0,
            CheckDigitPassed: false,
            DecodePath: ContainerOcrDecodePath.ManualQueueRequired,
            ModelId: loaded.ModelId,
            ModelVersion: loaded.ModelVersion);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_model is not null)
            {
                await _model.DisposeAsync().ConfigureAwait(false);
                _model = null;
            }
        }
        finally
        {
            _loadGate.Dispose();
        }
    }
}
