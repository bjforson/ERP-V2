namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// Decode path resolved by <see cref="IContainerNumberRecognizer.RecognizeAsync"/>.
/// Mirrors §6.1.7's safety-net spec but adapted for v2: there is no
/// Tesseract fallback in v2 (Tesseract is a v1-only concept), so the
/// safety net collapses to the manual-entry queue.
/// </summary>
public enum ContainerOcrDecodePath
{
    /// <summary>
    /// Florence-2 produced a check-digit-valid prediction with confidence
    /// at or above the configured gate. Caller should set
    /// <c>InspectionCase.subject_identifier</c> per §6.1.7.
    /// </summary>
    Primary,

    /// <summary>
    /// Florence-2 produced a prediction but the check digit failed or
    /// confidence fell below the configured gate. The workflow surfaces
    /// the manual-entry chip on the analyst console; <c>subject_identifier</c>
    /// stays null until an analyst confirms or types it in.
    /// </summary>
    ManualQueueRequired,
}
