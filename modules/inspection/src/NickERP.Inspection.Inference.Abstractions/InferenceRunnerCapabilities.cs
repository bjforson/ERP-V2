namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Capabilities the runner advertises to the admin UI and to model
/// validation. The host uses these to (a) reject incompatible model
/// promotions and (b) populate the runner picker.
/// </summary>
/// <param name="SupportsBatch">True if the runner can execute batched requests in one session call.</param>
/// <param name="SupportsDynamicShapes">True if the runner accepts inputs with at least one dynamic dimension at run time.</param>
/// <param name="SupportsFp16">True if fp16 model weights are supported on at least one available EP.</param>
/// <param name="SupportsInt8">True if int8 (QDQ or static-quantized) model weights are supported on at least one available EP.</param>
/// <param name="MaxModelSizeBytes">Hard upper bound on model artifact size the runner will load. <c>0</c> = no enforced limit.</param>
/// <param name="AvailableExecutionProviders">Execution providers detected at process start, in resolution-priority order (e.g. <c>["DirectML", "CPU"]</c>).</param>
public sealed record InferenceRunnerCapabilities(
    bool SupportsBatch,
    bool SupportsDynamicShapes,
    bool SupportsFp16,
    bool SupportsInt8,
    long MaxModelSizeBytes,
    IReadOnlyList<string> AvailableExecutionProviders);
