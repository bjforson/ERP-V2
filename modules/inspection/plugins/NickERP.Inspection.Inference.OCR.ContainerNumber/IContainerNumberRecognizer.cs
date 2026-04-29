namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// OCR-specific service contract for the §6.1 container-number recogniser.
/// A consumer of <see cref="NickERP.Inspection.Inference.Abstractions.IInferenceRunner"/>:
/// the recogniser owns plate-ROI preprocessing (resize-to-square + ImageNet
/// normalization), constrained-beam-search decoding restricted to the
/// ISO 6346 grammar (<c>[A-Z]{4}[0-9]{7}</c> ∪ <c>&lt;unreadable&gt;</c>), and
/// the ISO 6346 mod-11 check-digit gate. Inference itself is delegated to
/// the underlying runner so a Florence-2 ONNX export and a Donut ONNX
/// export are interchangeable behind the same model family
/// (<c>container-ocr-v1</c> per §6.1.7).
/// </summary>
public interface IContainerNumberRecognizer
{
    /// <summary>
    /// Recognise the ISO 6346 container number on a cropped plate ROI.
    /// </summary>
    /// <param name="plateRoiBytes">
    /// Raw image bytes of the plate ROI (PNG / JPEG / TIFF). The recogniser
    /// decodes, rescales long-edge to 384 px, zero-pads to a 384 × 384 square,
    /// and ImageNet-normalises into a <c>(3, 384, 384)</c> float32 tensor per
    /// §6.1.2. Caller retains ownership of the byte buffer.
    /// </param>
    /// <param name="correlationId">
    /// End-to-end correlation id propagated to the inference runner and
    /// emitted on the <c>inference.run</c> span.
    /// </param>
    /// <param name="tenantId">Resolved tenant for tenant-keyed model variants. Null = platform default.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ContainerNumberRecognition"/> carrying the predicted
    /// 11-character string (or <c>&lt;unreadable&gt;</c>), the model's mean-token
    /// softmax confidence, the boolean check-digit gate outcome, and the
    /// resolved <see cref="ContainerOcrDecodePath"/>.
    /// </returns>
    Task<ContainerNumberRecognition> RecognizeAsync(
        ReadOnlyMemory<byte> plateRoiBytes,
        string correlationId,
        int? tenantId,
        CancellationToken ct);
}
