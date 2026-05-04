// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// FailureModeClassifier — heuristic per plan §6.1.1 for the four buckets
// the eval JSON reports. This is intentionally best-effort: reliable
// classification needs labels we don't have at Sprint 19. Where heuristics
// can't classify a row, the row is excluded from per-bucket scoring (still
// counted in the overall rate). When the classifier produces fewer than
// MinBucketSamples per bucket on a corpus, the orchestrator emits null for
// the perFailureMode block and notes the limitation in the report.

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Buckets a corpus row into one of the §6.1.1 failure modes (or "none").
/// "None" rows still contribute to the overall accuracy rate — they're
/// just excluded from per-bucket reporting.
/// </summary>
internal static class FailureModeClassifier
{
    /// <summary>
    /// Minimum number of samples a bucket needs before its rate is
    /// reported. Below this threshold the rate is statistically
    /// meaningless. Tuned so a 5000-row run still produces useful
    /// numbers — most real corpora have skew across owner-prefix
    /// families.
    /// </summary>
    public const int MinBucketSamples = 30;

    /// <summary>
    /// Owner prefixes known to use stylized typography that breaks
    /// 1990s-vintage line-OCR engines. Sourced from §6.1.1 plus the BIC
    /// owner-prefix registry's stylization notes. Phase-2 expansion can
    /// pull this from a config file; for Sprint 19 the inline list is
    /// sufficient and locks the bar at "engines that fail on these
    /// prefixes lose disproportionately".
    /// </summary>
    private static readonly HashSet<string> StylizedPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HLBU", // Hapag-Lloyd serif
        "HLXU", // Hapag-Lloyd
        "MSCU", // MSC bold-condensed
        "MSDU", // MSC
        "TGHU", // TGH narrow stencil
        "TGBU", // TGH
        "EMCU", // Evergreen ornate
        "EGHU", // Evergreen
        "OOLU", // OOCL stylised
        "OOCU", // OOCL
    };

    /// <summary>
    /// Classify a row. Returns the bucket plus a short reason useful for
    /// the per-row debug log.
    /// </summary>
    public static (FailureBucket Bucket, string Reason) Classify(CorpusRow row)
    {
        // 1. Stylized typography — keyed off the truth's owner prefix when
        //    we have one (gold), else off the v1 prediction's prefix.
        var prefix = ExtractPrefix(row.OwnerPrefix)
                  ?? ExtractPrefix(row.Truth)
                  ?? ExtractPrefix(row.V1Prediction);
        if (prefix is not null && StylizedPrefixes.Contains(prefix))
        {
            return (FailureBucket.StylizedTypography, $"prefix={prefix}");
        }

        // 2. False-positive surfaces — when the corpus row's image_type
        //    indicates this is NOT a plate ROI (e.g. "side1", "side2"
        //    on FS6000 capture top-down whole containers, not plates).
        //    These rows test "did the engine refuse to invent text on
        //    something that isn't a plate?" The bucket inverts the
        //    scoring: a correct prediction would be the empty string,
        //    not an 11-char candidate.
        var imgType = row.ImageType?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(imgType)
            && imgType is "side1" or "side2" or "side"
            && string.IsNullOrEmpty(row.Truth))
        {
            return (FailureBucket.FalsePositiveSurfaces, $"img_type={imgType} no_truth");
        }

        // 3 & 4 (weathering, oblique-angles) require image-property
        //    inspection that needs labels we don't have at Sprint 19.
        //    Document the limitation: classify as None for now and the
        //    runbook explains.
        return (FailureBucket.None, "unbucketed");
    }

    private static string? ExtractPrefix(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 4) return null;
        var prefix = candidate[..4].ToUpperInvariant();
        for (var i = 0; i < 4; i++)
        {
            if (prefix[i] < 'A' || prefix[i] > 'Z') return null;
        }
        return prefix;
    }
}

internal enum FailureBucket
{
    /// <summary>Row not assigned to any per-bucket reporting category.</summary>
    None,
    StylizedTypography,
    Weathering,
    ObliqueAngles,
    FalsePositiveSurfaces,
}
