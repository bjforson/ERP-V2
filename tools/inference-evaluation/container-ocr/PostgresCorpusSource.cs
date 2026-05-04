// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// PostgresCorpusSource — read-only stream of plate-ROI rows from
// nickscan_production.fs6000images. Mirrors the SQL shape used by
// tools/inference-training/container-ocr/harvest_plates.py so the same
// rows the trainer sees feed the evaluator. Vendor names ("FS6000",
// "ICUMS") live ONLY in this adapter — every consumer downstream sees
// vendor-neutral CorpusRow.

using System.Globalization;
using Npgsql;

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Streams <see cref="CorpusRow"/>s from v1's <c>nickscan_production</c>
/// Postgres. Read-only by construction — every connection is opened
/// with <c>default_transaction_read_only = on</c> so accidental DDL
/// would be denied at the server level even if the harness were buggy.
///
/// SQL contract is the §12 Phase 3 cut: <c>fs6000images</c> JOIN
/// <c>fs6000scans</c> for <c>containernumber</c> + LEFT JOIN
/// <c>containerannotations</c> filtered for <c>type='ocr_correction'</c>
/// (the gold-label table). Image bytes live inline on
/// <c>fs6000images.imagedata</c> so the streaming side carries a real
/// byte[] per row.
/// </summary>
internal sealed class PostgresCorpusSource : ICorpusSource
{
    private readonly string _connStr;
    private readonly bool _skipNoV1Pred;
    private readonly NpgsqlDataSource _dataSource;
    private int _approxCount = -1;

    private const string SqlCount = """
        SELECT COUNT(*)
        FROM public.fs6000images i
        JOIN public.fs6000scans s ON s.id = i.scanid
        WHERE i.imagedata IS NOT NULL
          AND ($1 = false OR (s.containernumber IS NOT NULL AND length(trim(s.containernumber)) > 0))
    """;

    private const string SqlStream = """
        SELECT
            i.id::text                              AS image_id,
            i.imagedata                             AS image_bytes,
            i.imagetype                             AS image_type,
            s.containernumber                       AS v1_predicted,
            a.text                                  AS analyst_corrected,
            'FS6000'                                AS scanner_type
        FROM public.fs6000images i
        JOIN public.fs6000scans s ON s.id = i.scanid
        LEFT JOIN public.containerannotations a
            ON a.containernumber = s.containernumber
           AND a.type = 'ocr_correction'
           AND a.isdeleted IS NOT TRUE
        WHERE i.imagedata IS NOT NULL
          AND ($1 = false OR (s.containernumber IS NOT NULL AND length(trim(s.containernumber)) > 0))
        ORDER BY i.createdat NULLS LAST, i.id
        LIMIT $2
    """;

    public PostgresCorpusSource(string connStr, bool skipRowsWithoutV1Prediction)
    {
        _connStr = connStr;
        _skipNoV1Pred = skipRowsWithoutV1Prediction;
        var builder = new NpgsqlDataSourceBuilder(connStr);
        _dataSource = builder.Build();
    }

    public int ApproximateCount
    {
        get
        {
            if (_approxCount >= 0) return _approxCount;
            try
            {
                using var conn = _dataSource.OpenConnection();
                EnsureReadOnly(conn);
                using var cmd = new NpgsqlCommand(SqlCount, conn);
                cmd.Parameters.AddWithValue(_skipNoV1Pred);
                _approxCount = Convert.ToInt32(cmd.ExecuteScalar() ?? -1, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                _approxCount = -1;
            }
            return _approxCount;
        }
    }

    public IEnumerable<CorpusRow> Stream(int hardLimit, CancellationToken ct)
    {
        using var conn = _dataSource.OpenConnection();
        EnsureReadOnly(conn);
        using var cmd = new NpgsqlCommand(SqlStream, conn);
        cmd.Parameters.AddWithValue(_skipNoV1Pred);
        cmd.Parameters.AddWithValue(hardLimit > 0 ? hardLimit : 1_000_000);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var id = reader.GetString(0);
            var bytes = (byte[])reader.GetValue(1);
            var imageType = reader.IsDBNull(2) ? null : reader.GetString(2);
            var v1Predicted = reader.IsDBNull(3) ? null : reader.GetString(3)?.Trim();
            var analyst = reader.IsDBNull(4) ? null : reader.GetString(4)?.Trim();
            var scannerType = reader.IsDBNull(5) ? null : reader.GetString(5);

            // Truth precedence: analyst-corrected (gold) > v1 prediction
            // when it passes the ISO 6346 check (silver) > none.
            string? truth = null;
            if (!string.IsNullOrEmpty(analyst) && Iso6346Gate.IsValid(analyst))
            {
                truth = analyst;
            }
            else if (!string.IsNullOrEmpty(v1Predicted) && Iso6346Gate.IsValid(v1Predicted))
            {
                // SILVER: v1's Tesseract output that passed the check digit.
                // Including these as "truth" would be circular for a Tesseract
                // baseline measurement. We deliberately exclude silver from
                // the truth set when scoring the Tesseract engine — the
                // orchestrator decides per-engine whether to honour silver.
                // Emit it under V1Prediction so the orchestrator can branch.
                truth = null;
            }

            // Owner-prefix detection from the strongest available source.
            string? ownerPrefix = null;
            if (!string.IsNullOrEmpty(truth) && truth!.Length >= 4)
            {
                ownerPrefix = truth[..4];
            }
            else if (!string.IsNullOrEmpty(v1Predicted) && v1Predicted!.Length >= 4)
            {
                ownerPrefix = v1Predicted[..4];
            }

            yield return new CorpusRow(
                Id: id,
                ImageBytes: bytes,
                Truth: truth,
                V1Prediction: v1Predicted,
                OwnerPrefix: ownerPrefix,
                ImageType: imageType,
                ScannerType: scannerType);
        }
    }

    /// <summary>
    /// Force the session into read-only mode. Belt-and-braces: the harness
    /// only ever issues SELECTs, but if a future code path drifts, the
    /// server will reject DDL/DML at the transaction layer.
    /// </summary>
    private static void EnsureReadOnly(NpgsqlConnection conn)
    {
        using var cmd = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn);
        try { cmd.ExecuteNonQuery(); } catch { /* ignore — applies to next tx */ }
    }

    public void Dispose()
    {
        _dataSource.Dispose();
    }

    /// <summary>
    /// Build a connection string from the env-var convention shared with
    /// the sibling Python tools. Returns null when the password env var
    /// is not set — caller treats that as "Postgres source not available"
    /// and falls back to the directory source per Sprint 19's defensive
    /// check.
    /// </summary>
    public static string? ComposeFromEnvOrNull()
    {
        var pwd = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
        if (string.IsNullOrEmpty(pwd)) return null;
        // Same default as harvest_plates.py: localhost:5432, user=postgres,
        // db=nickscan_production. The harness intentionally hits the same
        // primary the production splitter does.
        return $"Host=localhost;Port=5432;Username=postgres;Password={pwd};Database=nickscan_production;Pooling=true;Timeout=15";
    }
}
