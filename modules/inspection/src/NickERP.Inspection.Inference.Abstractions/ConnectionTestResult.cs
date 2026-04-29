namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Outcome of an <see cref="IInferenceRunner.TestAsync"/> probe. Used by
/// admin UIs to validate that the requested execution provider is
/// actually available on the host before a model is promoted to
/// <c>active</c>.
/// </summary>
/// <param name="Success">True if the probe succeeded.</param>
/// <param name="Message">Human-readable detail (success summary or failure reason).</param>
/// <param name="Diagnostics">Optional structured diagnostics (e.g. <c>{ "ep.detected": "DirectML,CPU", "gpu.adapter": "Intel UHD" }</c>) for the admin UI to surface.</param>
public sealed record ConnectionTestResult(
    bool Success,
    string? Message,
    IReadOnlyDictionary<string, string>? Diagnostics);
