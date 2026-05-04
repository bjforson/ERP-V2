// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// CliOptions — parsed command-line arguments for the OCR eval harness.
// Lightweight argparse-style implementation (no Microsoft.Extensions.Configuration
// command-line provider) so the surface stays close to the sibling Python
// scripts (harvest_plates.py, export_splits.py).

using System.Globalization;

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Parsed command-line arguments for one harness run. Mirrors the contract
/// in the project README and locks defaults to plan §12.3 expectations.
/// </summary>
internal sealed record CliOptions
{
    /// <summary>OCR engine to evaluate. Tesseract is shipped Sprint 19; the
    /// other variants reserve enum values for the §6.1 Florence-2 / Donut
    /// integrations and are rejected with a clear message until ONNX
    /// exports land.</summary>
    public OcrEngineKind Engine { get; init; } = OcrEngineKind.Tesseract;

    /// <summary>Corpus source.</summary>
    public CorpusSourceKind CorpusSource { get; init; } = CorpusSourceKind.Postgres;

    /// <summary>Hard upper bound on number of corpus rows considered.
    /// 0 = unlimited. The §12.3 baseline run uses 5000 by default per the
    /// plan-file dispatch shape.</summary>
    public int CorpusLimit { get; init; } = 5000;

    /// <summary>Directory containing (.png + .json) ground-truth pairs when
    /// <see cref="CorpusSource"/> = Directory. Required for that mode.</summary>
    public string? CorpusDir { get; init; }

    /// <summary>Postgres connection string when CorpusSource = Postgres.
    /// When omitted, the harness composes one from
    /// <c>NICKSCAN_DB_PASSWORD</c> + <c>localhost:5432/nickscan_production</c>
    /// — same default as the sibling Python harvester.</summary>
    public string? ConnectionString { get; init; }

    /// <summary>Output JSON path. Refuses paths under v1's tree
    /// (<c>C:\Shared\NSCIM_PRODUCTION\</c>) — same invariant as the
    /// harvester.</summary>
    public string OutPath { get; init; } = "results/eval.json";

    /// <summary>Tessdata path (where eng.traineddata lives). Defaults to
    /// the v1 path on this box because Tesseract.NET has no other
    /// sane default; falls back to the bin tessdata if unset.</summary>
    public string? TessdataPath { get; init; }

    /// <summary>Skip rows where v1 already produced no prediction
    /// (<c>v1_predicted</c> is empty). Useful for direct floor measurement
    /// where we want to compare engines on the same bytes v1 chose to
    /// process; off by default to surface every input the production
    /// pipeline saw.</summary>
    public bool SkipRowsWithoutV1Prediction { get; init; }

    /// <summary>Don't actually run inference — print the resolved options
    /// and the corpus row count, then exit 0. Useful for CI smoke.</summary>
    public bool DryRun { get; init; }

    /// <summary>Hostname tag emitted in the JSON output. Defaults to
    /// <c>Environment.MachineName</c> with a "(CPU)" suffix when no
    /// runtime override is supplied.</summary>
    public string Host { get; init; } = $"{Environment.MachineName} (CPU)";

    public static CliOptions Parse(string[] args)
    {
        var engine = OcrEngineKind.Tesseract;
        var source = CorpusSourceKind.Postgres;
        var limit = 5000;
        string? corpusDir = null;
        string? connStr = null;
        var outPath = "results/eval.json";
        string? tessdataPath = null;
        var skipNoPred = false;
        var dryRun = false;
        string? hostOverride = null;

        var i = 0;
        while (i < args.Length)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--engine":
                    engine = ParseEngine(args[++i]);
                    break;
                case "--corpus-source":
                    source = ParseCorpusSource(args[++i]);
                    break;
                case "--corpus-limit":
                    limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--corpus-dir":
                    corpusDir = args[++i];
                    break;
                case "--connection-string":
                    connStr = args[++i];
                    break;
                case "--out":
                    outPath = args[++i];
                    break;
                case "--tessdata":
                    tessdataPath = args[++i];
                    break;
                case "--skip-rows-without-v1-prediction":
                    skipNoPred = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--host":
                    hostOverride = args[++i];
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{arg}'. Run with --help for usage.");
            }
            i++;
        }

        return new CliOptions
        {
            Engine = engine,
            CorpusSource = source,
            CorpusLimit = limit,
            CorpusDir = corpusDir,
            ConnectionString = connStr,
            OutPath = outPath,
            TessdataPath = tessdataPath,
            SkipRowsWithoutV1Prediction = skipNoPred,
            DryRun = dryRun,
            Host = hostOverride ?? $"{Environment.MachineName} (CPU)",
        };
    }

    private static OcrEngineKind ParseEngine(string s) => s.ToLowerInvariant() switch
    {
        "tesseract" => OcrEngineKind.Tesseract,
        "florence2" => OcrEngineKind.Florence2,
        "donut" => OcrEngineKind.Donut,
        _ => throw new ArgumentException(
            $"unknown engine '{s}'. Supported: tesseract, florence2, donut."),
    };

    private static CorpusSourceKind ParseCorpusSource(string s) => s.ToLowerInvariant() switch
    {
        "postgres" or "pg" => CorpusSourceKind.Postgres,
        "directory" or "dir" => CorpusSourceKind.Directory,
        _ => throw new ArgumentException(
            $"unknown corpus-source '{s}'. Supported: postgres, directory."),
    };

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            NickERP.Tools.OcrEvaluation — Sprint 19 offline OCR harness

            Usage:
              dotnet run --project tools/inference-evaluation/container-ocr -- \
                  --engine <tesseract|florence2|donut> \
                  --corpus-source <postgres|directory> \
                  [--corpus-limit N] \
                  [--corpus-dir PATH] \
                  [--connection-string CONN] \
                  [--out OUTFILE.json] \
                  [--tessdata TESSDATA_DIR] \
                  [--skip-rows-without-v1-prediction] \
                  [--dry-run] \
                  [--host HOST_TAG]

            Defaults:
              engine = tesseract; corpus-source = postgres; corpus-limit = 5000
              out = results/eval.json
              connection-string composed from NICKSCAN_DB_PASSWORD against
              localhost:5432/nickscan_production (same convention as the
              sibling Python harvester).

            Exit codes:
              0  success (or successful --dry-run)
              2  bad CLI args
              3  refusing to write under v1 tree (read-only invariant)
              4  corpus source unavailable (e.g. db unreachable)
              5  no corpus rows
              6  engine not yet supported (florence2 / donut)
              7  tessdata not found
            """);
    }
}

internal enum OcrEngineKind
{
    Tesseract,
    Florence2,
    Donut,
}

internal enum CorpusSourceKind
{
    Postgres,
    Directory,
}
