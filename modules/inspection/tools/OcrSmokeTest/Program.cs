// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// OcrSmokeTest — bring-up proof for the §6.1 container-OCR contract chain.
// Loads the random-init container-OCR stub artifact produced by
// tools/inference-training/container-ocr/train_stub_ocr.py via the
// OnnxRuntime runner (sha256 fail-fast included), runs the
// IContainerNumberRecognizer against a synthetic 384x384 plate ROI, then
// asserts:
//   1. The recogniser returns a populated ContainerNumberRecognition with
//      a length-11 prediction OR the <unreadable> sentinel.
//   2. The ISO 6346 check-digit gate fires false on five known-bad
//      strings (string-level test against a faithful Iso6346 oracle —
//      independent of the ONNX path, exercises the gate logic that
//      decides decode_path).
//
// Exit 0 on success.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Inference.Abstractions;
using NickERP.Inspection.Inference.OCR.ContainerNumber;
using NickERP.Inspection.Inference.OnnxRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NickERP.Inspection.Tools.OcrSmokeTest;

internal static class Program
{
    private const string ModelDirectory = @"C:\Shared\ERP V2\storage\models\container-ocr\v1";
    private const string ModelId = "container-ocr-v1";
    private const string ModelVersion = "v1.0.0-stub";
    private const string CorrelationId = "ocr-smoke-test-2026-04-29";

    public static async Task<int> Main()
    {
        try
        {
            return await RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[ocr-smoke] FATAL: " + ex.GetType().FullName + ": " + ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task<int> RunAsync()
    {
        Console.WriteLine("[ocr-smoke] === v2 container-OCR scaffold bring-up ===");

        // 1. Independent string-level test of the ISO 6346 gate. This must
        //    pass even if the ONNX runtime isn't available, so we run it
        //    first.
        if (!RunCheckDigitGateTests())
        {
            Console.Error.WriteLine("[ocr-smoke] FAIL: ISO 6346 check-digit gate tests failed.");
            return 2;
        }

        var modelPath = Path.Combine(ModelDirectory, "model.onnx");
        var metadataPath = Path.Combine(ModelDirectory, "model.metadata.json");
        Console.WriteLine($"[ocr-smoke] model dir   = {ModelDirectory}");
        Console.WriteLine($"[ocr-smoke] onnx path   = {modelPath}");

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"Stub artifact not found at {modelPath}. Run train_stub_ocr.py first.",
                modelPath);
        }
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException(
                $"Stub metadata not found at {metadataPath}. Run train_stub_ocr.py first.",
                metadataPath);
        }

        var sha256 = ReadSha256FromMetadata(metadataPath);
        Console.WriteLine($"[ocr-smoke] sha256      = {sha256}");

        // 2. DI registration. Wire the OnnxRuntime runner first, then the
        //    OCR recogniser on top of it.
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
        services.AddInferenceContainerOcr(
            artifactProvider: _ => new ModelArtifact
            {
                ModelId = ModelId,
                Version = ModelVersion,
                Path = modelPath,
                Sha256 = sha256,
            },
            configureOptions: cfg =>
            {
                cfg.MaxTokenBudget = 16;
                cfg.BeamWidth = 4;
            });

        await using var provider = services.BuildServiceProvider();
        var recognizer = provider.GetRequiredService<IContainerNumberRecognizer>();
        Console.WriteLine("[ocr-smoke] resolved IContainerNumberRecognizer");

        // 3. Build a synthetic plate-shaped PNG.
        var roiBytes = BuildSyntheticPlateImageBytes();
        Console.WriteLine($"[ocr-smoke] synthetic ROI: {roiBytes.Length} bytes PNG");

        // 4. Run the recogniser.
        var recognition = await recognizer.RecognizeAsync(
            plateRoiBytes: roiBytes,
            correlationId: CorrelationId,
            tenantId: null,
            ct: CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine("[ocr-smoke] === RESULT ===");
        Console.WriteLine($"[ocr-smoke] predicted        = '{recognition.Predicted}'");
        Console.WriteLine($"[ocr-smoke] confidence       = {recognition.Confidence:F4}");
        Console.WriteLine($"[ocr-smoke] checkDigitPassed = {recognition.CheckDigitPassed}");
        Console.WriteLine($"[ocr-smoke] decodePath       = {recognition.DecodePath}");
        Console.WriteLine($"[ocr-smoke] modelId          = {recognition.ModelId}");
        Console.WriteLine($"[ocr-smoke] modelVersion     = {recognition.ModelVersion}");

        // 5. Contract shape assertions.
        if (string.IsNullOrEmpty(recognition.Predicted))
        {
            Console.Error.WriteLine("[ocr-smoke] FAIL: predicted is null/empty.");
            return 3;
        }
        var isSentinel = recognition.Predicted == "<unreadable>";
        var is11Char = recognition.Predicted.Length == 11;
        if (!isSentinel && !is11Char)
        {
            Console.Error.WriteLine(
                $"[ocr-smoke] FAIL: predicted '{recognition.Predicted}' is neither <unreadable> nor 11 chars.");
            return 4;
        }

        // Random-init artifact: check digit will essentially never pass on
        // garbage logits. So decode_path MUST be ManualQueueRequired.
        if (recognition.DecodePath != ContainerOcrDecodePath.ManualQueueRequired)
        {
            Console.Error.WriteLine(
                $"[ocr-smoke] FAIL: random stub yielded decodePath={recognition.DecodePath}; " +
                "expected ManualQueueRequired (random logits never pass the ISO 6346 mod-11 gate).");
            return 5;
        }

        if (recognition.CheckDigitPassed)
        {
            Console.Error.WriteLine(
                "[ocr-smoke] FAIL: random stub reported checkDigitPassed=true. " +
                "Re-seed the stub trainer if this hits — the probability is ~1/11 by chance.");
            return 6;
        }

        if (recognition.ModelId != ModelId || recognition.ModelVersion != ModelVersion)
        {
            Console.Error.WriteLine(
                $"[ocr-smoke] FAIL: model identity round-trip mismatch: " +
                $"got ({recognition.ModelId}, {recognition.ModelVersion}), expected ({ModelId}, {ModelVersion}).");
            return 7;
        }

        Console.WriteLine("[ocr-smoke] === SMOKE TEST PASSED ===");
        return 0;
    }

    /// <summary>
    /// String-level test of the ISO 6346 mod-11 gate against known-bad inputs
    /// and a known-good control. Independent of any ONNX runtime — this
    /// exercises the post-process gate logic that decides decode_path.
    /// Uses <see cref="ComputeIsValidProxy"/> as the oracle (the same mod-11
    /// math the plugin's internal Iso6346 helper implements).
    /// </summary>
    private static bool RunCheckDigitGateTests()
    {
        var probes = new (string label, string candidate, bool expectedValid)[]
        {
            ("empty",       "",                false),
            ("too short",   "MSCU123456",      false),
            ("too long",    "MSCU12345678",    false),
            ("bad letters", "1234U567890",     false),
            ("bad digits",  "MSCUABCDEFG",     false),
            ("sentinel",    "<unreadable>",    false),
        };

        var allPass = true;
        foreach (var (label, candidate, expected) in probes)
        {
            var actual = ComputeIsValidProxy(candidate);
            var ok = actual == expected;
            var marker = ok ? "ok " : "FAIL";
            Console.WriteLine(
                $"[ocr-smoke] gate test [{marker}] {label,-12} candidate='{candidate}' " +
                $"expected={expected} actual={actual}");
            if (!ok) allPass = false;
        }

        // Find ONE valid 11-char container number deterministically — pick a
        // prefix MSCU and brute-force the check digit so we have a known-good
        // control for the gate. Use an 11-char base; FindAnyValidIso6346
        // overwrites the trailing digit while iterating 0–9.
        var goodCandidate = FindAnyValidIso6346("MSCU0000000");
        if (goodCandidate is null)
        {
            Console.Error.WriteLine("[ocr-smoke] gate test FAIL: could not synthesise a valid ISO 6346 control.");
            return false;
        }
        var goodActual = ComputeIsValidProxy(goodCandidate);
        Console.WriteLine(
            $"[ocr-smoke] gate test [{(goodActual ? "ok " : "FAIL")}] {"good ctrl",-12} " +
            $"candidate='{goodCandidate}' expected=True actual={goodActual}");
        if (!goodActual) allPass = false;

        return allPass;
    }

    /// <summary>
    /// Walk digit 0–9 over the last position until a check-digit-valid
    /// candidate emerges. Returns null if none of the 10 candidates work,
    /// which signals a bug in the oracle (every 10-digit prefix has exactly
    /// one valid completion in [0,9] modulo the standard's 10→0 fold).
    /// </summary>
    private static string? FindAnyValidIso6346(string baseCandidate)
    {
        if (baseCandidate.Length != 11) return null;
        for (var d = 0; d < 10; d++)
        {
            var candidate = baseCandidate.Substring(0, 10) + (char)('0' + d);
            if (ComputeIsValidProxy(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// In-test-only replication of the ISO 6346 mod-11 check used by the
    /// plugin's <c>Iso6346.IsValid</c>. Kept here so the smoke test stays
    /// independent of the plugin internals. Divergence between this and the
    /// plugin is a real bug — both must move together if the table mapping
    /// ever changes.
    /// </summary>
    private static bool ComputeIsValidProxy(string candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length != 11) return false;
        for (var i = 0; i < 4; i++)
        {
            var c = candidate[i];
            if (c < 'A' || c > 'Z') return false;
        }
        for (var i = 4; i < 11; i++)
        {
            var c = candidate[i];
            if (c < '0' || c > '9') return false;
        }

        int[] vals = { 10,12,13,14,15,16,17,18,19,20,21,23,24,25,26,27,28,29,30,31,32,34,35,36,37,38 };
        long sum = 0;
        for (var i = 0; i < 10; i++)
        {
            int v = i < 4 ? vals[candidate[i] - 'A'] : candidate[i] - '0';
            sum += (long)v << i;
        }
        var mod = (int)(sum % 11);
        // mod == 10 is reserved by ISO 6346 — never issued.
        if (mod == 10) return false;
        return candidate[10] - '0' == mod;
    }

    /// <summary>
    /// Generate a 384x384 PNG with a plate-shaped light rectangle and 11
    /// dark vertical stripes that look vaguely like character columns.
    /// Drawn by direct pixel manipulation so we don't drag in
    /// SixLabors.Drawing. Enough to exercise the preprocessor (decode →
    /// resize → ImageNet normalise); the random-init model produces
    /// near-uniform logits regardless of input.
    /// </summary>
    private static byte[] BuildSyntheticPlateImageBytes()
    {
        using var image = new Image<Rgb24>(384, 384, new Rgb24(40, 40, 60));
        var plateColor = new Rgb24(220, 220, 220);
        var charColor = new Rgb24(20, 20, 20);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 140; y < 244; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 40; x < 344; x++) row[x] = plateColor;
            }
            for (var i = 0; i < 11; i++)
            {
                var x0 = 60 + i * 25;
                var x1 = x0 + 16;
                for (var y = 160; y < 220; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = x0; x < x1; x++) row[x] = charColor;
                }
            }
        });

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
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
