// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// InferenceSmokeTest — bring-up proof for the §3 / §4 inference contract
// chain. Loads the random-init container-split stub artifact produced by
// tools/inference-bringup/train_stub_split.py via the OnnxRuntime runner
// (sha256 fail-fast included), runs one inference on a (1, 1, 472, 1568)
// zeros tensor, prints the resolved EP, output shape, first 10 output
// values, and the InferenceMetrics, then exits. Not a unit test — a
// console exe that writes its own report.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using NickERP.Inspection.Inference.Abstractions;
using NickERP.Inspection.Inference.OnnxRuntime;
using OrtTensorElementType = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;

namespace NickERP.Inspection.Tools.InferenceSmokeTest;

internal static class Program
{
    private const string DefaultModelDirectory = @"C:\Shared\ERP V2\storage\models\container-split\v1";
    private const string ModelId = "container-split";
    private const string DefaultModelVersion = "v1.0.0-stub";
    private const string CorrelationId = "smoke-test-2026-04-29";

    // §3.2 input shape: (N, 1, 472, 1568) float32. Batch 1 here.
    private const int Channels = 1;
    private const int Height = 472;
    private const int Width = 1568;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[smoke-test] FATAL: " + ex.GetType().FullName + ": " + ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static (string modelDir, string modelVersion, int? iterations) ParseArgs(string[] args)
    {
        // Optional argv overrides so the same exe can be pointed at the v2
        // artifact produced by tools/inference-training/container-split/.
        // Falls back to the env vars NICKERP_SMOKE_MODEL_DIR /
        // NICKERP_SMOKE_MODEL_VERSION / NICKERP_SMOKE_ITERATIONS so a CI
        // wrapper can override without re-parsing.
        var modelDir =
            Environment.GetEnvironmentVariable("NICKERP_SMOKE_MODEL_DIR") ?? DefaultModelDirectory;
        var modelVersion =
            Environment.GetEnvironmentVariable("NICKERP_SMOKE_MODEL_VERSION") ?? DefaultModelVersion;
        int? iterations = null;
        if (int.TryParse(
                Environment.GetEnvironmentVariable("NICKERP_SMOKE_ITERATIONS"),
                out var envIters))
        {
            iterations = envIters;
        }
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model-dir":
                    modelDir = args[++i];
                    break;
                case "--model-version":
                    modelVersion = args[++i];
                    break;
                case "--iterations":
                    iterations = int.Parse(args[++i]);
                    break;
            }
        }
        return (modelDir, modelVersion, iterations);
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var (modelDir, modelVersion, iterations) = ParseArgs(args);

        Console.WriteLine("[smoke-test] === v2 inference scaffold bring-up ===");
        Console.WriteLine($"[smoke-test] model_id      = {ModelId}");
        Console.WriteLine($"[smoke-test] version       = {modelVersion}");
        Console.WriteLine($"[smoke-test] model dir     = {modelDir}");
        if (iterations is not null)
        {
            Console.WriteLine($"[smoke-test] iterations    = {iterations} (p95 inference timing requested)");
        }

        var modelPath = Path.Combine(modelDir, "model.onnx");
        var metadataPath = Path.Combine(modelDir, "model.metadata.json");
        Console.WriteLine($"[smoke-test] onnx path     = {modelPath}");
        Console.WriteLine($"[smoke-test] metadata path = {metadataPath}");

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Stub artifact not found at {modelPath}. Run train_stub_split.py first.",
                modelPath);
        }
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException(
                $"Stub metadata not found at {metadataPath}. Run train_stub_split.py first.",
                metadataPath);
        }

        var sha256 = ReadSha256FromMetadata(metadataPath);
        Console.WriteLine($"[smoke-test] sha256        = {sha256}");

        // 1. DI registration. Direct DI wiring per §4.5 — production hosts
        // would discover the runner via NickERP.Platform.Plugins, but for
        // the bring-up smoke test the AddInferenceOnnxRuntime extension is
        // the convenience path documented in ServiceCollectionExtensions.
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(opt =>
            {
                opt.SingleLine = true;
                opt.IncludeScopes = false;
                opt.TimestampFormat = "HH:mm:ss.fff ";
            });
        });
        services.AddInferenceOnnxRuntime();

        await using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<object>>();
        logger.LogInformation("ServiceProvider built; resolving IInferenceRunner.");

        var runner = provider.GetRequiredKeyedService<IInferenceRunner>(
            ServiceCollectionExtensions.ServiceKey);
        Console.WriteLine($"[smoke-test] runner.TypeCode = {runner.TypeCode}");
        Console.WriteLine(
            "[smoke-test] runner.Capabilities = " +
            $"SupportsBatch={runner.Capabilities.SupportsBatch}, " +
            $"SupportsDynamicShapes={runner.Capabilities.SupportsDynamicShapes}, " +
            $"SupportsFp16={runner.Capabilities.SupportsFp16}, " +
            $"SupportsInt8={runner.Capabilities.SupportsInt8}, " +
            $"AvailableEPs=[{string.Join(", ", runner.Capabilities.AvailableExecutionProviders)}]");

        // 2. Build the artifact + load options.
        var artifact = new ModelArtifact
        {
            ModelId = ModelId,
            Version = modelVersion,
            Path = modelPath,
            Sha256 = sha256,
        };

        // Pick the first available EP from the runner's detected list, falling
        // back to CPU. CPU is always present so this is a no-fallback path
        // when DirectML is missing.
        var preferredEp = runner.Capabilities.AvailableExecutionProviders.Contains("DirectML")
            ? "DirectML"
            : "CPU";
        var loadOptions = new ModelLoadOptions(
            PreferredExecutionProvider: preferredEp,
            GpuDeviceId: null,
            UseInt8: false,
            WarmupOnLoad: true);
        Console.WriteLine($"[smoke-test] load.PreferredExecutionProvider = {preferredEp}");
        Console.WriteLine($"[smoke-test] load.WarmupOnLoad = {loadOptions.WarmupOnLoad}");

        Console.WriteLine("[smoke-test] runner.LoadAsync(...) — verifying sha256 and opening session ...");
        var loaded = await runner.LoadAsync(artifact, loadOptions, CancellationToken.None)
            .ConfigureAwait(false);
        await using var loadedAsync = loaded;

        Console.WriteLine($"[smoke-test] LOADED. EP resolved   = {loaded.ExecutionProviderUsed}");
        Console.WriteLine($"[smoke-test] LOADED. ModelId       = {loaded.ModelId}");
        Console.WriteLine($"[smoke-test] LOADED. ModelVersion  = {loaded.ModelVersion}");
        Console.WriteLine($"[smoke-test] LOADED. Inputs:");
        foreach (var input in loaded.Metadata.Inputs)
        {
            Console.WriteLine(
                "  - name='" + input.Name + "', dtype=" + input.ElementType +
                ", shape=" + FormatShape(input.Shape));
        }
        Console.WriteLine("[smoke-test] LOADED. Outputs:");
        foreach (var output in loaded.Metadata.Outputs)
        {
            Console.WriteLine(
                "  - name='" + output.Name + "', dtype=" + output.ElementType +
                ", shape=" + FormatShape(output.Shape));
        }

        // 3. Build a synthetic (1, 1, 472, 1568) float32 input — zeros are
        // fine, this is a contract-chain smoke test, not an accuracy test.
        var inputDescriptor = loaded.Metadata.Inputs.Single();
        var inputName = inputDescriptor.Name;

        Console.WriteLine(
            $"[smoke-test] building synthetic input '{inputName}' " +
            $"shape=(1, {Channels}, {Height}, {Width}) zeros float32");

        // OrtValue.CreateAllocatedTensorValue allocates a CPU-side tensor
        // of the right shape and zero-fills it. OnnxTensor takes ownership
        // and disposes it for us.
        var shape = new long[] { 1, Channels, Height, Width };
        var ortValue = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance,
            OrtTensorElementType.Float,
            shape);
        ITensor inputTensor = new OnnxTensor(ortValue);

        var request = new InferenceRequest
        {
            Inputs = new Dictionary<string, ITensor> { [inputName] = inputTensor },
            CorrelationId = CorrelationId,
            TenantId = null,
            Timeout = null,
        };

        try
        {
            Console.WriteLine($"[smoke-test] runner.RunAsync(correlation_id={CorrelationId}) ...");
            var result = await loaded.RunAsync(request, CancellationToken.None).ConfigureAwait(false);

            // 4. Print outputs.
            Console.WriteLine("[smoke-test] === RESULT ===");
            Console.WriteLine($"[smoke-test] result.Outputs.Count = {result.Outputs.Count}");

            foreach (var (name, tensor) in result.Outputs)
            {
                Console.WriteLine($"[smoke-test] output '{name}': dtype={tensor.ElementType}, shape={FormatShape(tensor.Shape)}");

                if (tensor.ElementType == TensorElementType.Float32)
                {
                    var data = tensor.AsSpan<float>();
                    var preview = Math.Min(10, data.Length);
                    var firstValues = new float[preview];
                    for (var i = 0; i < preview; i++) firstValues[i] = data[i];
                    Console.WriteLine(
                        $"[smoke-test] output '{name}' length={data.Length} first {preview}: " +
                        "[" + string.Join(", ", firstValues.Select(v => v.ToString("0.######"))) + "]");
                }
                else
                {
                    Console.WriteLine($"[smoke-test] output '{name}' element type {tensor.ElementType} not previewed.");
                }
            }

            // 5. Metrics.
            var m = result.Metrics;
            Console.WriteLine(
                "[smoke-test] metrics: " +
                $"preprocess={m.PreprocessUs}us, " +
                $"inference={m.InferenceUs}us, " +
                $"postprocess={m.PostprocessUs}us, " +
                $"ep={m.ExecutionProviderUsed}, " +
                $"peakBytes={m.PeakBytesAllocated}");

            // Dispose output tensors per InferenceResult contract.
            foreach (var (_, t) in result.Outputs) t.Dispose();

            // Optional p95 timing per §3.6 inference budget. Re-runs the
            // same input N times and reports the inference-only p95.
            if (iterations is int iters && iters > 1)
            {
                Console.WriteLine($"[smoke-test] timing loop: {iters} iterations on {loaded.ExecutionProviderUsed}");
                var samples = new long[iters];
                for (var i = 0; i < iters; i++)
                {
                    var rep = await loaded.RunAsync(request, CancellationToken.None).ConfigureAwait(false);
                    samples[i] = rep.Metrics.InferenceUs;
                    foreach (var (_, t) in rep.Outputs) t.Dispose();
                }
                Array.Sort(samples);
                var p50 = samples[samples.Length / 2];
                var p95 = samples[(int)Math.Floor(samples.Length * 0.95)];
                var p99 = samples[(int)Math.Floor(samples.Length * 0.99)];
                Console.WriteLine(
                    "[smoke-test] timing: " +
                    $"p50={p50}us, p95={p95}us, p99={p99}us, n={samples.Length}");
            }
        }
        finally
        {
            inputTensor.Dispose();
        }

        Console.WriteLine("[smoke-test] === SMOKE TEST PASSED ===");
        return 0;
    }

    private static string FormatShape(IReadOnlyList<int?> shape)
    {
        return "(" + string.Join(", ", shape.Select(d => d.HasValue ? d.Value.ToString() : "?")) + ")";
    }

    private static string FormatShape(IReadOnlyList<int> shape)
    {
        return "(" + string.Join(", ", shape) + ")";
    }

    [SuppressMessage("Performance", "CA1869:Cache and reuse 'JsonSerializerOptions' instances",
        Justification = "Single-call console exe.")]
    private static string ReadSha256FromMetadata(string metadataPath)
    {
        using var fs = File.OpenRead(metadataPath);
        using var doc = JsonDocument.Parse(fs);
        if (!doc.RootElement.TryGetProperty("sha256", out var sha))
        {
            throw new InvalidDataException(
                $"metadata.json missing required 'sha256' property. Path: {metadataPath}.");
        }
        var value = sha.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"metadata.json 'sha256' is empty. Path: {metadataPath}.");
        }
        return value;
    }
}
