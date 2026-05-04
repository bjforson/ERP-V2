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
/// SQL contract — joins <c>fs6000images</c> (image bytes inline on
/// <c>imagedata</c>) to <c>fs6000scans</c> for the scanner-supplied
/// <c>containernumber</c>. That field is the FS6000 vendor XML
/// manifest's <c>container_no</c> element (see v1
/// <c>NickScanCentralImagingPortal.Services.FS6000.XmlParsingService.cs</c>
/// line 534) — ie. it's external scanner truth, NOT v1's Tesseract
/// output. v1's Tesseract path runs against the rendered LUT image and
/// produces an extracted candidate that's compared to this expected
/// truth, but the truth itself is independent of any OCR.
///
/// Note: the harvester at
/// <c>tools/inference-training/container-ocr/harvest_plates.py</c>
/// describes a <c>containerannotations</c> table with
/// <c>type='ocr_correction'</c> for analyst-corrected gold labels.
/// On this production box (probed 2026-05-04) that table holds 74 rows,
/// all <c>type='Rectangle'</c> bbox annotations — there are NO
/// OCR-correction rows. So the only available truth source is
/// <c>fs6000scans.containernumber</c>. We accept that and document
/// the caveat in the runbook.
/// </summary>
internal sealed class PostgresCorpusSource : ICorpusSource
{
    private readonly string _connStr;
    private readonly bool _skipNoV1Pred;
    private readonly NpgsqlDataSource _dataSource;
    private int _approxCount = -1;

    // imagetype = 'Main' is the LUT-rendered 8-bit JPEG composite — the
    // exact input v1's ContainerNumberOcrService receives via
    // FS6000ImagePipeline.GetCompleteContainerDataAsync. The other types
    // (HighEnergy / LowEnergy / Material) are raw 16-bit channels never
    // fed to v1's Tesseract path; running OCR over them would inflate
    // the miss rate without measuring v1's reality.
    private const string SqlCount = """
        SELECT COUNT(*)
        FROM public.fs6000images i
        JOIN public.fs6000scans s ON s.id = i.scanid
        WHERE i.imagedata IS NOT NULL
          AND i.imagetype = 'Main'
          AND ($1 = false OR (s.containernumber IS NOT NULL AND length(trim(s.containernumber)) > 0))
    """;

    private const string SqlStream = """
        SELECT
            i.id::text                              AS image_id,
            i.imagedata                             AS image_bytes,
            i.imagetype                             AS image_type,
            s.containernumber                       AS scanner_truth,
            'FS6000'                                AS scanner_type
        FROM public.fs6000images i
        JOIN public.fs6000scans s ON s.id = i.scanid
        WHERE i.imagedata IS NOT NULL
          AND i.imagetype = 'Main'
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
            var scannerTruth = reader.IsDBNull(3) ? null : reader.GetString(3)?.Trim();
            var scannerType = reader.IsDBNull(4) ? null : reader.GetString(4);

            // Truth selection on this corpus: fs6000scans.containernumber
            // is the FS6000 vendor manifest's container_no — independent of
            // any Tesseract pass. We accept it as truth when it passes the
            // ISO 6346 mod-11 check digit (filters out manual-entry typos
            // and edge cases). Below the gate, we set truth=null so the
            // row is excluded from scoring rather than producing a
            // false negative against bad data.
            string? truth = null;
            if (!string.IsNullOrEmpty(scannerTruth) && Iso6346Gate.IsValid(scannerTruth))
            {
                truth = scannerTruth;
            }

            string? ownerPrefix = null;
            if (!string.IsNullOrEmpty(truth) && truth!.Length >= 4)
            {
                ownerPrefix = truth[..4];
            }

            yield return new CorpusRow(
                Id: id,
                ImageBytes: bytes,
                Truth: truth,
                // V1Prediction stays null — we don't have a stored Tesseract
                // output column on this corpus. The Tesseract baseline IS
                // this harness's run; v1's run isn't persisted anywhere
                // queryable.
                V1Prediction: null,
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
