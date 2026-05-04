// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// TesseractEngine — point-in-time port of the v1
// ContainerNumberOcrService preprocessing + OCR chain (Tesseract 5.2.0).
// v1 is read-only, so this is a straight copy into the harness rather
// than a reference. Divergence between this file and v1's
// NickScanCentralImagingPortal.Services.ImageProcessing.ContainerNumberOcrService
// would invalidate the baseline measurement, so the regex + preprocess
// pipeline is held byte-for-byte equivalent to v1's.

using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenCvSharp;
using Tesseract;

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Wraps Tesseract.NET against ISO 6346 plate ROIs. One engine per
/// process — Tesseract's <c>TesseractEngine</c> is not safe to share
/// across threads, but the harness scores serially anyway, so a single
/// instance is fine.
/// </summary>
internal sealed class TesseractEngine : IOcrEngine
{
    private const string ContainerPattern = @"([A-Z]{4})(\d{7})";
    private static readonly string[] AlternatePatterns = new[]
    {
        @"([A-Z]{4})\s*(\d{7})",
        @"([A-Z]{4})-(\d{7})",
        @"([A-Z]{4})\.(\d{7})",
    };

    private readonly Tesseract.TesseractEngine _engine;

    public TesseractEngine(string tessdataPath)
    {
        if (!File.Exists(Path.Combine(tessdataPath, "eng.traineddata")))
        {
            throw new FileNotFoundException(
                $"Tesseract language data not found: {Path.Combine(tessdataPath, "eng.traineddata")}. " +
                "Download eng.traineddata from https://github.com/tesseract-ocr/tessdata and " +
                "place it under the --tessdata directory.");
        }
        _engine = new Tesseract.TesseractEngine(tessdataPath, "eng", EngineMode.Default);
    }

    public OcrEngineKind Kind => OcrEngineKind.Tesseract;

    public OcrEngineResult Recognise(byte[] imageBytes)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var preprocessed = PreprocessForOcr(imageBytes);
            using var pix = Pix.LoadFromMemory(preprocessed);
            using var page = _engine.Process(pix);
            var raw = page.GetText() ?? string.Empty;
            var conf = page.GetMeanConfidence(); // [0,1]
            var extracted = ExtractContainerNumber(raw) ?? raw.Trim();
            sw.Stop();
            return new OcrEngineResult(extracted, conf, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception)
        {
            sw.Stop();
            // Keep the harness moving on individual-row failures — they
            // count as misses at the orchestrator layer.
            return new OcrEngineResult(string.Empty, -1, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// v1's preprocessing chain (Otsu + Gaussian + CLAHE). Held byte-for-
    /// byte equivalent to v1 so the baseline reflects production reality.
    /// </summary>
    private static byte[] PreprocessForOcr(byte[] imageBytes)
    {
        try
        {
            using var src = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (src.Empty()) return imageBytes;

            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

            using var blurred = new Mat();
            Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);

            using var threshold = new Mat();
            Cv2.Threshold(blurred, threshold, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

            using var clahe = Cv2.CreateCLAHE(2.0, new Size(8, 8));
            using var enhanced = new Mat();
            clahe.Apply(threshold, enhanced);

            var encodeParams = new int[] { (int)ImwriteFlags.JpegQuality, 95 };
            Cv2.ImEncode(".jpg", enhanced, out var result, encodeParams);
            return result;
        }
        catch (Exception)
        {
            return imageBytes;
        }
    }

    /// <summary>
    /// Extract a candidate ISO 6346 string from raw Tesseract text.
    /// Mirrors v1's pattern set: the canonical no-separator pattern plus
    /// the with-space / with-dash / with-dot variants.
    /// </summary>
    private static string? ExtractContainerNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, ContainerPattern, RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value.ToUpperInvariant() + match.Groups[2].Value;
        }
        foreach (var pattern in AlternatePatterns)
        {
            match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpperInvariant() + match.Groups[2].Value;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _engine.Dispose();
    }
}
