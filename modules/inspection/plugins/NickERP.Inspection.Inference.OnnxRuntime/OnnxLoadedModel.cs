using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using NickERP.Inspection.Inference.Abstractions;
using ModelMetadata = NickERP.Inspection.Inference.Abstractions.ModelMetadata;

namespace NickERP.Inspection.Inference.OnnxRuntime;

/// <summary>
/// <see cref="ILoadedModel"/> wrapping a <see cref="InferenceSession"/>.
/// One instance per active <c>(model_id, version)</c>; held by the host
/// as a singleton, hot-swapped via atomic ref swap on model promotion.
/// </summary>
public sealed class OnnxLoadedModel : ILoadedModel
{
    private static readonly double TicksPerMicrosecond = Stopwatch.Frequency / 1_000_000.0;

    private readonly InferenceSession _session;
    private readonly ILogger<OnnxLoadedModel>? _logger;
    private readonly RunOptions _runOptions;
    private int _disposed;

    /// <param name="session">Open <see cref="InferenceSession"/> the loaded model takes ownership of.</param>
    /// <param name="modelId">Logical model id (e.g. <c>container-split</c>).</param>
    /// <param name="modelVersion">Artifact version (e.g. <c>v3.1.0</c>).</param>
    /// <param name="metadata">Runner-extracted metadata; cached for the lifetime of the loaded model.</param>
    /// <param name="executionProviderUsed">EP that actually serviced this session, e.g. <c>DirectML</c>.</param>
    /// <param name="logger">Optional logger; null falls back to no logging.</param>
    public OnnxLoadedModel(
        InferenceSession session,
        string modelId,
        string modelVersion,
        ModelMetadata metadata,
        string executionProviderUsed,
        ILogger<OnnxLoadedModel>? logger)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        ModelVersion = modelVersion ?? throw new ArgumentNullException(nameof(modelVersion));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        ExecutionProviderUsed = executionProviderUsed ?? throw new ArgumentNullException(nameof(executionProviderUsed));
        _logger = logger;
        _runOptions = new RunOptions();
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public string ModelVersion { get; }

    /// <inheritdoc />
    public ModelMetadata Metadata { get; }

    /// <inheritdoc />
    public string ExecutionProviderUsed { get; }

    /// <inheritdoc />
    public async Task<InferenceResult> RunAsync(InferenceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(OnnxLoadedModel));

        // ORT's session.Run is synchronous from the managed surface — wrap in
        // Task.Run so the caller's await point stays cooperative under load.
        // We pre-package the OrtValue dictionary on the calling thread so any
        // ITensor mismatches surface before the worker switches contexts.
        return await Task.Run(() => RunSync(request, ct), ct).ConfigureAwait(false);
    }

    private InferenceResult RunSync(InferenceRequest request, CancellationToken ct)
    {
        var preStart = Stopwatch.GetTimestamp();

        // Build the input dictionary — these are borrowed references, the
        // caller owns the underlying tensors.
        var inputs = new Dictionary<string, OrtValue>(request.Inputs.Count);
        foreach (var (name, tensor) in request.Inputs)
        {
            if (tensor is OnnxTensor onnx)
            {
                inputs[name] = onnx.OrtValue;
            }
            else
            {
                throw new InvalidOperationException(
                    $"OnnxRuntimeRunner expects OnnxTensor inputs (port '{name}' was {tensor.GetType().Name}). " +
                    "Adapter ITensors must be marshalled into OrtValues by the host before calling RunAsync.");
            }
        }

        var outputNames = new List<string>(Metadata.Outputs.Count);
        foreach (var d in Metadata.Outputs) outputNames.Add(d.Name);

        ct.ThrowIfCancellationRequested();
        var preEnd = Stopwatch.GetTimestamp();

        // Cooperative cancellation — RunOptions.Terminate signals the
        // session to abort at its next op boundary.
        using var ctRegistration = ct.Register(static state => ((RunOptions)state!).Terminate = true, _runOptions);

        var runStart = Stopwatch.GetTimestamp();
        IDisposableReadOnlyCollection<OrtValue> outputs;
        try
        {
            outputs = _session.Run(_runOptions, inputs, outputNames);
        }
        catch (OnnxRuntimeException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Inference cancelled.", ex, ct);
        }
        var runEnd = Stopwatch.GetTimestamp();

        // Build the output dictionary; the OnnxTensors take ownership of
        // each OrtValue so the caller disposes them after postprocess.
        var postStart = Stopwatch.GetTimestamp();
        var outputDict = new Dictionary<string, ITensor>(outputs.Count);
        try
        {
            for (var i = 0; i < outputs.Count && i < outputNames.Count; i++)
            {
                outputDict[outputNames[i]] = new OnnxTensor(outputs[i]);
            }
        }
        catch
        {
            foreach (var v in outputs) v.Dispose();
            throw;
        }
        var postEnd = Stopwatch.GetTimestamp();

        return new InferenceResult
        {
            Outputs = outputDict,
            Metrics = new InferenceMetrics(
                PreprocessUs: ToMicroseconds(preEnd - preStart),
                InferenceUs: ToMicroseconds(runEnd - runStart),
                PostprocessUs: ToMicroseconds(postEnd - postStart),
                ExecutionProviderUsed: ExecutionProviderUsed,
                PeakBytesAllocated: 0)
        };
    }

    private static long ToMicroseconds(long ticks) =>
        TicksPerMicrosecond <= 0 ? 0 : (long)(ticks / TicksPerMicrosecond);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        try
        {
            _runOptions.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OnnxLoadedModel.DisposeAsync: RunOptions.Dispose threw.");
        }
        try
        {
            _session.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OnnxLoadedModel.DisposeAsync: InferenceSession.Dispose threw.");
        }
        return ValueTask.CompletedTask;
    }
}
