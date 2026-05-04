// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// DirectoryCorpusSource — fallback corpus source for when the live
// Postgres is unavailable. Reads (.png + .json) ground-truth pairs from
// a directory. Schema for the JSON sidecars matches what
// tools/v1-label-export/export_splits.py emits per row plus a few
// container-OCR-specific fields. See README for an example.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Reads OCR eval rows from a directory containing image files paired
/// with same-stem JSON sidecars. Useful when the production Postgres is
/// unreachable (off-network laptop, sandbox CI) or when the corpus is a
/// curated golden set rather than a production sample.
///
/// Layout:
/// <code>
///   ocr-eval/
///     row-001.png
///     row-001.json     {"truth":"MSCU1234567","ownerPrefix":"MSCU","imageType":"top","scannerType":"FS6000"}
///     row-002.png
///     row-002.json
/// </code>
/// </summary>
internal sealed class DirectoryCorpusSource : ICorpusSource
{
    private readonly string _dir;
    private readonly List<string> _imageFiles;

    public DirectoryCorpusSource(string dir)
    {
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"corpus directory does not exist: {dir}");
        }
        _dir = dir;
        _imageFiles = Directory.EnumerateFiles(dir, "*.*")
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".png" or ".jpg" or ".jpeg" or ".tif" or ".tiff" or ".bmp";
            })
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    public int ApproximateCount => _imageFiles.Count;

    public IEnumerable<CorpusRow> Stream(int hardLimit, CancellationToken ct)
    {
        var emitted = 0;
        foreach (var imgPath in _imageFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (hardLimit > 0 && emitted >= hardLimit) yield break;

            var stem = Path.GetFileNameWithoutExtension(imgPath);
            var jsonPath = Path.Combine(_dir, stem + ".json");
            DirectorySidecar? sidecar = null;
            if (File.Exists(jsonPath))
            {
                using var fs = File.OpenRead(jsonPath);
                sidecar = JsonSerializer.Deserialize<DirectorySidecar>(fs);
            }

            yield return new CorpusRow(
                Id: stem,
                ImageBytes: File.ReadAllBytes(imgPath),
                Truth: sidecar?.Truth,
                V1Prediction: sidecar?.V1Prediction,
                OwnerPrefix: sidecar?.OwnerPrefix ?? ExtractPrefix(sidecar?.Truth),
                ImageType: sidecar?.ImageType,
                ScannerType: sidecar?.ScannerType);

            emitted++;
        }
    }

    private static string? ExtractPrefix(string? truth)
    {
        if (string.IsNullOrEmpty(truth) || truth.Length < 4) return null;
        var prefix = truth[..4].ToUpperInvariant();
        for (var i = 0; i < 4; i++)
        {
            if (prefix[i] < 'A' || prefix[i] > 'Z') return null;
        }
        return prefix;
    }

    public void Dispose() { /* nothing to release */ }

    private sealed record DirectorySidecar
    {
        [JsonPropertyName("truth")] public string? Truth { get; init; }
        [JsonPropertyName("v1Prediction")] public string? V1Prediction { get; init; }
        [JsonPropertyName("ownerPrefix")] public string? OwnerPrefix { get; init; }
        [JsonPropertyName("imageType")] public string? ImageType { get; init; }
        [JsonPropertyName("scannerType")] public string? ScannerType { get; init; }
    }
}
