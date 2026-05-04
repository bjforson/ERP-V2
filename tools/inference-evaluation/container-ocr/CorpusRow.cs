// Copyright (c) Nick TC-Scan Ltd. All rights reserved.
//
// CorpusRow — one OCR-evaluation input. Vendor-neutral shape: every corpus
// source (Postgres FS6000Images, directory of .png+.json pairs, future
// synthetic generator) hydrates this same record before the harness sees it.

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// One input to the OCR engine plus its ground-truth answer (when known).
/// Vendor names (FS6000, ICUMS, etc.) belong in the <see cref="CorpusSource"/>
/// implementation, not in this contract.
/// </summary>
/// <param name="Id">Stable identifier for the row (e.g. fs6000images.id, or
/// directory file stem). Emitted in the per-row debug log; never in the
/// summary JSON.</param>
/// <param name="ImageBytes">Plate-ROI image bytes. PNG/JPEG/TIFF — the
/// engine decodes.</param>
/// <param name="Truth">The ISO-6346 ground-truth value when known. The
/// preferred source is analyst-corrected (gold). Empty/null means
/// "no ground truth" — these rows are excluded from accuracy scoring
/// entirely.</param>
/// <param name="V1Prediction">v1 Tesseract's recorded prediction for this
/// row. Carries the value v1 already wrote to the production DB.
/// Used for two purposes: (1) optional skip-when-empty filter to scope
/// the eval to rows v1 actually OCRed, (2) cross-check the Tesseract
/// engine's reproduction against v1's recorded output.</param>
/// <param name="OwnerPrefix">Cached 4-letter owner prefix from the truth
/// when available; null otherwise. Used by the failure-mode classifier
/// for stylized-typography flagging.</param>
/// <param name="ImageType">Free-form scanner image-type tag (e.g.
/// "top", "side1"). Helps the failure-mode classifier disambiguate
/// false-positive surfaces vs. proper plate ROIs. May be empty.</param>
/// <param name="ScannerType">Free-form scanner type tag (e.g.
/// "FS6000", "ASE"). Stylized-typography thresholds may diverge per
/// scanner family; carried for diagnostic logging.</param>
internal sealed record CorpusRow(
    string Id,
    byte[] ImageBytes,
    string? Truth,
    string? V1Prediction,
    string? OwnerPrefix,
    string? ImageType,
    string? ScannerType);
