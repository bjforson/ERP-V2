namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// Outcome of one <see cref="IContainerNumberRecognizer.RecognizeAsync"/>
/// call. Maps 1:1 onto the §6.1.2 <c>Finding</c> payload.
/// </summary>
/// <param name="Predicted">
/// Predicted ISO 6346 container number, e.g. <c>"MSCU1234567"</c>. The
/// sentinel <c>"&lt;unreadable&gt;"</c> is returned when the constrained beam
/// search exhausts its budget without producing a valid 11-character
/// sequence (§6.1.8 decoder-loop guard).
/// </param>
/// <param name="Confidence">
/// Mean per-token softmax confidence over the predicted sequence
/// (§6.1.5 / Q-D4). Range <c>[0, 1]</c>. Owner-prefix-not-in-BIC-registry
/// handling clamps this value to <c>min(confidence, 0.7)</c> upstream
/// per §6.1.8 — that clamp is applied here when an optional prefix
/// registry is wired in.
/// </param>
/// <param name="CheckDigitPassed">
/// Outcome of the ISO 6346 mod-11 check-digit gate (§6.1.7). Always
/// <c>false</c> when <see cref="Predicted"/> is <c>"&lt;unreadable&gt;"</c>.
/// </param>
/// <param name="DecodePath">
/// Resolved decode path. <c>Primary</c> when the gate passes and confidence
/// ≥ the configured threshold; <c>ManualQueueRequired</c> in all other
/// cases. v1's Tesseract fallback is intentionally not modelled here:
/// v2 has no Tesseract.
/// </param>
/// <param name="ModelId">Logical model id used (matches <c>InferenceModel.id</c>).</param>
/// <param name="ModelVersion">Artifact version used (matches <c>InferenceModel.version</c>).</param>
public sealed record ContainerNumberRecognition(
    string Predicted,
    double Confidence,
    bool CheckDigitPassed,
    ContainerOcrDecodePath DecodePath,
    string ModelId,
    string ModelVersion);
