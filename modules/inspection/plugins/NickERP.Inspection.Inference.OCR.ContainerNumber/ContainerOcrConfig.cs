namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// Wireable config for <see cref="ContainerNumberRecognizer"/>. Mirrors the
/// schema declared in <c>plugin.json</c>; loaded at registration time and
/// frozen for the recogniser's lifetime. Hot-swap of model_id/version
/// happens via <see cref="NickERP.Inspection.Inference.Abstractions.IInferenceRunner"/>
/// (§4.5), not this config — this is just where the recogniser learns
/// which logical model id to ask the runner for.
/// </summary>
public sealed class ContainerOcrConfig
{
    /// <summary>
    /// Logical model id passed into <c>IInferenceRunner.LoadAsync</c>.
    /// §6.1.7 fixes this at <c>container-ocr-v1</c> for the first ship;
    /// the suffix bumps when the input/output contract breaks (rare).
    /// </summary>
    public string ModelId { get; set; } = "container-ocr-v1";

    /// <summary>SemVer of the loaded artifact.</summary>
    public string ModelVersion { get; set; } = "v1.0.0";

    /// <summary>
    /// Mean-token-softmax confidence threshold below which
    /// <see cref="ContainerOcrDecodePath.ManualQueueRequired"/> is forced.
    /// §6.1.7 defaults this to <c>0.85</c>; §6.1.8 raises to <c>0.92</c>
    /// when ROI detector reports >20 % occlusion (caller-side override).
    /// </summary>
    public double ConfidenceGate { get; set; } = 0.85;

    /// <summary>
    /// Constrained beam search width. <c>4</c> is the recommended default;
    /// raising it to <c>8</c> trades ~1.4× decoder latency for ~0.3 pp EM.
    /// </summary>
    public int BeamWidth { get; set; } = 4;

    /// <summary>
    /// Hard token budget T per §6.1.8 ("Decoder loop / repetition" guard).
    /// 11 characters + EOS + slack = 16. Anything above this triggers the
    /// <c>&lt;unreadable&gt;</c> sentinel.
    /// </summary>
    public int MaxTokenBudget { get; set; } = 16;

    /// <summary>Square input edge in pixels. §6.1.2 fixes 384.</summary>
    public int InputHeightPx { get; set; } = 384;

    /// <summary>Square input edge in pixels. §6.1.2 fixes 384.</summary>
    public int InputWidthPx { get; set; } = 384;
}
