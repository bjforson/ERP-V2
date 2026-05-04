// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// EvalReport — JSON shape consumed by the §12.3 acceptance gate. Locked to
// the schema in plan-file §12 and reproduced verbatim in the project README.
// Adding fields requires updating both.

using System.Text.Json.Serialization;

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Output of one harness run. Serialised to JSON with
/// <see cref="System.Text.Json.JsonSerializerOptions.WriteIndented"/> = true
/// for human-readability — the file is committed to the repo as the
/// permanent floor measurement.
/// </summary>
internal sealed record EvalReport
{
    [JsonPropertyName("engine")]
    public required string Engine { get; init; }

    [JsonPropertyName("corpusSize")]
    public required int CorpusSize { get; init; }

    [JsonPropertyName("scoredRows")]
    public required int ScoredRows { get; init; }

    [JsonPropertyName("exactMatchRate")]
    public required double ExactMatchRate { get; init; }

    [JsonPropertyName("checkDigitPassRate")]
    public required double CheckDigitPassRate { get; init; }

    [JsonPropertyName("perFailureMode")]
    public PerFailureModeRates? PerFailureMode { get; init; }

    [JsonPropertyName("latency")]
    public required LatencyPercentiles Latency { get; init; }

    [JsonPropertyName("ranAt")]
    public required string RanAt { get; init; }

    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

/// <summary>
/// Per-failure-mode exact-match rates. Each rate is the fraction of rows
/// in that bucket on which the engine produced an exactly-matching
/// prediction. <c>null</c> at the parent level when reliable
/// classification was not possible (e.g. owner-prefix metadata missing).
/// </summary>
internal sealed record PerFailureModeRates
{
    /// <summary>Stylized owner-prefix typography (Hapag HLBU, MSC MSCU,
    /// TGHU stencil, etc.). Bucketed by owner-prefix family.</summary>
    [JsonPropertyName("stylizedTypography")]
    public double StylizedTypography { get; init; }

    /// <summary>Paint runs / weathering. Heuristic — see
    /// <see cref="FailureModeClassifier"/> for the bucketing rule.</summary>
    [JsonPropertyName("weathering")]
    public double Weathering { get; init; }

    /// <summary>Plates rotated > 8 deg from horizontal.</summary>
    [JsonPropertyName("obliqueAngles")]
    public double ObliqueAngles { get; init; }

    /// <summary>Inputs where the engine confidently emits a candidate
    /// from a non-plate surface (door corrugation shadows, etc.). Lower
    /// is better for this bucket.</summary>
    [JsonPropertyName("falsePositiveSurfaces")]
    public double FalsePositiveSurfaces { get; init; }

    /// <summary>Per-bucket sample counts. Helps readers gauge how
    /// representative each rate is.</summary>
    [JsonPropertyName("bucketCounts")]
    public required PerFailureModeCounts BucketCounts { get; init; }
}

internal sealed record PerFailureModeCounts
{
    [JsonPropertyName("stylizedTypography")]
    public int StylizedTypography { get; init; }

    [JsonPropertyName("weathering")]
    public int Weathering { get; init; }

    [JsonPropertyName("obliqueAngles")]
    public int ObliqueAngles { get; init; }

    [JsonPropertyName("falsePositiveSurfaces")]
    public int FalsePositiveSurfaces { get; init; }
}

internal sealed record LatencyPercentiles
{
    [JsonPropertyName("p50Ms")]
    public required double P50Ms { get; init; }

    [JsonPropertyName("p95Ms")]
    public required double P95Ms { get; init; }

    [JsonPropertyName("p99Ms")]
    public required double P99Ms { get; init; }

    [JsonPropertyName("meanMs")]
    public required double MeanMs { get; init; }

    [JsonPropertyName("samples")]
    public required int Samples { get; init; }
}
