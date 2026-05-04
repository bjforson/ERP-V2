// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// Program — entry point. Wires the corpus source + engine + evaluator
// using Microsoft.Extensions.Hosting + DI. Same shape as the rest of the
// v2 console tools.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NickERP.Tools.OcrEvaluation;

internal static class Program
{
    private const string V1ForbiddenPrefix = @"c:\shared\nscim_production";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (ArgumentException ax)
        {
            Console.Error.WriteLine("[ocr-eval] FATAL: " + ax.Message);
            CliOptions.PrintHelp();
            return ExitCodes.BadArgs;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[ocr-eval] FATAL: " + ex.GetType().FullName + ": " + ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Task<int> RunAsync(string[] args)
    {
        var opts = CliOptions.Parse(args);

        // Read-only invariant: refuse to write under v1's tree.
        var resolved = Path.GetFullPath(opts.OutPath).ToLowerInvariant().Replace('/', '\\');
        if (resolved.StartsWith(V1ForbiddenPrefix, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[ocr-eval] FATAL: --out {opts.OutPath} resolves under v1's tree ({V1ForbiddenPrefix}). " +
                "v1 is read-only during v2 dev. Write the JSON under v2 (e.g. C:/Shared/ERP V2/tools/...) or /tmp.");
            return Task.FromResult(ExitCodes.ForbiddenOut);
        }

        // DI / logging
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.IncludeScopes = false;
                o.TimestampFormat = "HH:mm:ss.fff ";
            });
        });
        services.AddSingleton(opts);

        using var sp = services.BuildServiceProvider();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var log = loggerFactory.CreateLogger("ocr-eval");

        log.LogInformation("=== NickERP.Tools.OcrEvaluation — Sprint 19 ===");
        log.LogInformation(
            "engine={Engine} corpus_source={Source} corpus_limit={Limit} out={Out} dry_run={Dry}",
            opts.Engine, opts.CorpusSource, opts.CorpusLimit, opts.OutPath, opts.DryRun);

        // Engine support gate (florence2 / donut not yet supported).
        if (opts.Engine != OcrEngineKind.Tesseract)
        {
            Console.Error.WriteLine(
                $"[ocr-eval] FATAL: engine '{opts.Engine}' is reserved for the Florence-2 / Donut " +
                "integration tracks (§6.1 / plan §12). The corresponding ONNX exports do not exist " +
                "yet — re-run with --engine tesseract for the Sprint 19 baseline.");
            return Task.FromResult(ExitCodes.EngineUnsupported);
        }

        // Build the corpus source.
        ICorpusSource source;
        if (opts.CorpusSource == CorpusSourceKind.Postgres)
        {
            var conn = opts.ConnectionString ?? PostgresCorpusSource.ComposeFromEnvOrNull();
            if (string.IsNullOrEmpty(conn))
            {
                Console.Error.WriteLine(
                    "[ocr-eval] FATAL: Postgres corpus source selected but no connection string available. " +
                    "Set NICKSCAN_DB_PASSWORD env var or pass --connection-string. " +
                    "Or fall back to --corpus-source directory --corpus-dir <path>.");
                return Task.FromResult(ExitCodes.CorpusUnavailable);
            }
            try
            {
                source = new PostgresCorpusSource(conn, opts.SkipRowsWithoutV1Prediction);
                var probe = source.ApproximateCount;
                if (probe < 0)
                {
                    log.LogWarning("Postgres corpus probe returned -1 (count not available). Streaming will reveal real volume.");
                }
                else
                {
                    log.LogInformation("Postgres corpus approximate count: {Count}", probe);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ocr-eval] FATAL: cannot open Postgres corpus: {ex.Message}");
                return Task.FromResult(ExitCodes.CorpusUnavailable);
            }
        }
        else
        {
            if (string.IsNullOrEmpty(opts.CorpusDir))
            {
                Console.Error.WriteLine("[ocr-eval] FATAL: --corpus-source directory requires --corpus-dir PATH.");
                return Task.FromResult(ExitCodes.BadArgs);
            }
            source = new DirectoryCorpusSource(opts.CorpusDir);
            log.LogInformation("Directory corpus row count: {Count}", source.ApproximateCount);
        }

        if (opts.DryRun)
        {
            log.LogInformation("--dry-run set: skipping inference. Exit 0.");
            source.Dispose();
            return Task.FromResult(ExitCodes.Ok);
        }

        // Build the engine.
        var tessdataPath = ResolveTessdataPath(opts.TessdataPath);
        if (string.IsNullOrEmpty(tessdataPath))
        {
            Console.Error.WriteLine(
                "[ocr-eval] FATAL: tessdata directory not found. Pass --tessdata <dir-with-eng.traineddata> " +
                "or place eng.traineddata under <bin>/tessdata/.");
            source.Dispose();
            return Task.FromResult(ExitCodes.TessdataNotFound);
        }
        log.LogInformation("Tessdata: {Tessdata}", tessdataPath);
        using var engine = new TesseractEngine(tessdataPath);

        // Run.
        var evaluator = new Evaluator(loggerFactory.CreateLogger<Evaluator>(), engine, source);
        var report = evaluator.Run(opts.CorpusLimit, opts.Host, CancellationToken.None);

        // Write the JSON.
        var outPath = Path.GetFullPath(opts.OutPath);
        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
        File.WriteAllText(outPath, json);
        log.LogInformation("Wrote report: {OutPath}", outPath);

        // Decision-gate read-out per plan §12.3 + Sprint 19 brief.
        var em = report.ExactMatchRate;
        if (em > 0.92)
        {
            log.LogWarning(
                "Decision gate: Tesseract baseline EM={Em:P2} > 92%. Florence-2 ROI shrinks. " +
                "Surface to user before locking §6.1 in pilot scope.", em);
        }
        else if (em >= 0.60)
        {
            log.LogInformation(
                "Decision gate: Tesseract baseline EM={Em:P2} in 60-92%. Florence-2 has clear room.", em);
        }
        else if (em > 0)
        {
            log.LogInformation(
                "Decision gate: Tesseract baseline EM={Em:P2} below 60%. Florence-2 absolutely justified.", em);
        }

        source.Dispose();
        return Task.FromResult(ExitCodes.Ok);
    }

    private static string? ResolveTessdataPath(string? cliPath)
    {
        if (!string.IsNullOrEmpty(cliPath))
        {
            return File.Exists(Path.Combine(cliPath, "eng.traineddata")) ? cliPath : null;
        }
        // Common locations: <bin>/tessdata/, <cwd>/tessdata/, or v1's
        // canonical publish path. Last is a convenience for the dev box.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
            @"C:\Shared\NSCIM_PRODUCTION\publish\API\tessdata",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "eng.traineddata")))
            {
                return c;
            }
        }
        return null;
    }
}
