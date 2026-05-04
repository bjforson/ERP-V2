// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// Evaluator — orchestrates one harness run. Streams rows from the corpus
// source, runs each through the OCR engine, scores against ground truth,
// classifies failure mode, and accumulates the latency / accuracy stats
// that materialise into the EvalReport.

using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NickERP.Tools.OcrEvaluation;

internal sealed class Evaluator
{
    private readonly ILogger<Evaluator> _logger;
    private readonly IOcrEngine _engine;
    private readonly ICorpusSource _source;

    public Evaluator(ILogger<Evaluator> logger, IOcrEngine engine, ICorpusSource source)
    {
        _logger = logger;
        _engine = engine;
        _source = source;
    }

    public EvalReport Run(int hardLimit, string host, CancellationToken ct)
    {
        var ranAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var totalRows = 0;
        var scoredRows = 0;
        var exactMatches = 0;
        var checkDigitPasses = 0;
        var latencies = new List<double>(capacity: Math.Min(hardLimit, 10_000));

        // Per-bucket stats
        var bucketCount = new Dictionary<FailureBucket, int>();
        var bucketHits = new Dictionary<FailureBucket, int>();
        foreach (FailureBucket b in Enum.GetValues<FailureBucket>())
        {
            bucketCount[b] = 0;
            bucketHits[b] = 0;
        }

        var startUtc = DateTime.UtcNow;
        var lastProgressUtc = startUtc;
        var noTruthRows = 0;

        foreach (var row in _source.Stream(hardLimit, ct))
        {
            ct.ThrowIfCancellationRequested();
            totalRows++;

            var result = _engine.Recognise(row.ImageBytes);
            latencies.Add(result.LatencyMs);

            var normalised = Iso6346Gate.Normalise(result.RawText);
            var checkPassed = Iso6346Gate.IsValid(normalised);
            if (checkPassed) checkDigitPasses++;

            // Score only when ground truth is present.
            if (string.IsNullOrEmpty(row.Truth))
            {
                noTruthRows++;
            }
            else
            {
                scoredRows++;
                var hit = string.Equals(normalised, row.Truth, StringComparison.Ordinal);
                if (hit) exactMatches++;

                var (bucket, _) = FailureModeClassifier.Classify(row);
                bucketCount[bucket] += 1;
                if (bucket == FailureBucket.FalsePositiveSurfaces)
                {
                    // Inverted scoring: a "hit" on the false-positive bucket
                    // is when the engine refused to invent text (raw output
                    // is empty AFTER normalisation).
                    if (string.IsNullOrEmpty(normalised)) bucketHits[bucket] += 1;
                }
                else if (hit)
                {
                    bucketHits[bucket] += 1;
                }
            }

            // Progress every ~1000 rows or 5s, whichever comes first.
            if (totalRows % 1000 == 0
                || (DateTime.UtcNow - lastProgressUtc).TotalSeconds >= 5)
            {
                lastProgressUtc = DateTime.UtcNow;
                var elapsed = (DateTime.UtcNow - startUtc).TotalSeconds;
                _logger.LogInformation(
                    "progress rows={Total} scored={Scored} em={Em} cd={Cd} elapsed={Elapsed:F1}s",
                    totalRows, scoredRows, exactMatches, checkDigitPasses, elapsed);
            }
        }

        var (p50, p95, p99, mean) = ComputePercentiles(latencies);
        var perFailure = BuildPerFailureMode(bucketCount, bucketHits);

        var report = new EvalReport
        {
            Engine = _engine.Kind.ToString().ToLowerInvariant(),
            CorpusSize = totalRows,
            ScoredRows = scoredRows,
            ExactMatchRate = scoredRows == 0 ? 0.0 : (double)exactMatches / scoredRows,
            CheckDigitPassRate = totalRows == 0 ? 0.0 : (double)checkDigitPasses / totalRows,
            PerFailureMode = perFailure,
            Latency = new LatencyPercentiles
            {
                P50Ms = p50,
                P95Ms = p95,
                P99Ms = p99,
                MeanMs = mean,
                Samples = latencies.Count,
            },
            RanAt = ranAt,
            Host = host,
            Notes = BuildNotes(totalRows, scoredRows, noTruthRows, perFailure is null),
        };

        _logger.LogInformation(
            "run complete rows={Total} scored={Scored} no_truth={NoTruth} em_rate={Em:P2} cd_rate={Cd:P2} p50={P50:F0}ms p95={P95:F0}ms",
            totalRows, scoredRows, noTruthRows,
            report.ExactMatchRate, report.CheckDigitPassRate,
            report.Latency.P50Ms, report.Latency.P95Ms);

        return report;
    }

    private static PerFailureModeRates? BuildPerFailureMode(
        Dictionary<FailureBucket, int> count,
        Dictionary<FailureBucket, int> hits)
    {
        var stylized = count[FailureBucket.StylizedTypography];
        var weather = count[FailureBucket.Weathering];
        var oblique = count[FailureBucket.ObliqueAngles];
        var fakes = count[FailureBucket.FalsePositiveSurfaces];

        // Drop the per-failure-mode block entirely when no bucket reached
        // the minimum-sample bar. Better to surface "we couldn't classify"
        // than to publish a noisy 0.0 rate that misleads downstream.
        var anyBucketUsable =
            stylized >= FailureModeClassifier.MinBucketSamples ||
            weather  >= FailureModeClassifier.MinBucketSamples ||
            oblique  >= FailureModeClassifier.MinBucketSamples ||
            fakes    >= FailureModeClassifier.MinBucketSamples;
        if (!anyBucketUsable)
        {
            return null;
        }

        return new PerFailureModeRates
        {
            StylizedTypography = stylized == 0 ? 0.0 : (double)hits[FailureBucket.StylizedTypography] / stylized,
            Weathering         = weather  == 0 ? 0.0 : (double)hits[FailureBucket.Weathering]         / weather,
            ObliqueAngles      = oblique  == 0 ? 0.0 : (double)hits[FailureBucket.ObliqueAngles]      / oblique,
            FalsePositiveSurfaces = fakes == 0 ? 0.0 : (double)hits[FailureBucket.FalsePositiveSurfaces] / fakes,
            BucketCounts = new PerFailureModeCounts
            {
                StylizedTypography = stylized,
                Weathering = weather,
                ObliqueAngles = oblique,
                FalsePositiveSurfaces = fakes,
            },
        };
    }

    private static (double P50, double P95, double P99, double Mean) ComputePercentiles(IReadOnlyList<double> samples)
    {
        if (samples.Count == 0) return (0, 0, 0, 0);
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        return (
            P50: Percentile(sorted, 0.50),
            P95: Percentile(sorted, 0.95),
            P99: Percentile(sorted, 0.99),
            Mean: sorted.Average());
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[idx];
    }

    private static string? BuildNotes(int total, int scored, int noTruth, bool perFailureSkipped)
    {
        var notes = new List<string>();
        if (total == 0)
        {
            notes.Add("Corpus produced zero rows.");
        }
        if (scored == 0 && total > 0)
        {
            notes.Add($"All {total} rows lacked ground truth — accuracy stats are zero by definition.");
        }
        else if (noTruth > 0)
        {
            notes.Add($"{noTruth}/{total} rows had no ground truth and were excluded from accuracy scoring.");
        }
        if (perFailureSkipped)
        {
            notes.Add(
                $"Per-failure-mode rates suppressed: no bucket reached the {FailureModeClassifier.MinBucketSamples}-sample minimum.");
        }
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }
}
