// Copyright (c) Nick TC-Scan Ltd. All rights reserved.

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Engine-agnostic OCR contract used by the evaluation harness. Concrete
/// engines (Tesseract today, Florence-2 / Donut later) implement this
/// shape so the orchestrator can swap them without re-wiring.
///
/// Distinct from <see cref="NickERP.Inspection.Inference.OCR.ContainerNumber.IContainerNumberRecognizer"/>:
/// the recogniser is the v2 production plugin contract that requires
/// preprocessing + check-digit gating + decode-path resolution.
/// This evaluator shape is dumber on purpose — it returns the raw
/// extracted string and a per-row latency, and the harness post-processes
/// (normalise, gate, score). That keeps Tesseract, Florence-2, and Donut
/// comparable on identical scoring rules even though their guarantees
/// differ.
/// </summary>
internal interface IOcrEngine : IDisposable
{
    /// <summary>Engine identity emitted in the JSON report's
    /// <c>engine</c> field.</summary>
    OcrEngineKind Kind { get; }

    /// <summary>Run inference on one ROI. Implementations should not
    /// throw on unrecognisable inputs — return an empty / sentinel value
    /// instead so the harness can score "engine produced nothing" as a
    /// miss (not a fatal error).</summary>
    /// <returns>Raw extracted text. Pre-normalisation. Caller normalises
    /// via <see cref="Iso6346Gate.Normalise"/> before scoring.</returns>
    OcrEngineResult Recognise(byte[] imageBytes);
}

/// <summary>Per-row engine output.</summary>
/// <param name="RawText">Unprocessed engine output. Empty when no
/// candidate was produced.</param>
/// <param name="Confidence">Engine-reported confidence in [0,1] when
/// available. Tesseract's per-word confidence is averaged. -1 sentinel
/// means "engine doesn't expose a confidence."</param>
/// <param name="LatencyMs">Wall-clock latency from input to output.</param>
internal sealed record OcrEngineResult(
    string RawText,
    double Confidence,
    double LatencyMs);
