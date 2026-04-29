namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Plugin contract every inference runner implements. Concrete classes are
/// decorated with <c>[NickERP.Platform.Plugins.Plugin("type-code")]</c> and
/// shipped as a sibling DLL + plugin.json under the inspection host's
/// plugins folder. Runners load model artifacts (typically ONNX, but the
/// surface is runtime-agnostic) and execute inference behind an
/// <see cref="ILoadedModel"/> handle.
/// </summary>
public interface IInferenceRunner
{
    /// <summary>Stable identifier persisted in <c>InferenceModel.runner_type_code</c>; matches the <c>[Plugin]</c> attribute on the concrete class.</summary>
    string TypeCode { get; }

    /// <summary>Capabilities the host needs to know about (batch / dynamic shapes / fp16 / int8, available execution providers).</summary>
    InferenceRunnerCapabilities Capabilities { get; }

    /// <summary>Probe the runner's environment (e.g. that the requested EP is actually available). Cheap; admin UI calls it.</summary>
    Task<ConnectionTestResult> TestAsync(
        InferenceRunnerConfig config,
        CancellationToken ct);

    /// <summary>
    /// Open a model artifact and return a hot, ready-to-run <see cref="ILoadedModel"/>.
    /// The runner SHOULD verify <see cref="ModelArtifact.Sha256"/> against the
    /// on-disk bytes before opening the session and fail fast on mismatch.
    /// If <see cref="ModelLoadOptions.WarmupOnLoad"/> is true the runner
    /// SHOULD execute one synthetic inference (using a metadata-derived
    /// dummy input) before returning, so first real request avoids the
    /// 2–5 s session-init tax.
    /// </summary>
    Task<ILoadedModel> LoadAsync(
        ModelArtifact artifact,
        ModelLoadOptions options,
        CancellationToken ct);
}
