namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Per-call timings and EP attribution captured by the runner. Phase
/// timings use a monotonic clock (<c>Stopwatch.GetTimestamp</c>) and
/// expose microseconds so the histogram bucket boundaries on
/// <c>inference_latency_seconds</c> remain stable.
/// </summary>
/// <param name="PreprocessUs">Wall-clock microseconds spent on host-side preprocess (e.g. tensor packing) before <c>session.Run</c>.</param>
/// <param name="InferenceUs">Wall-clock microseconds spent inside the runtime's <c>session.Run</c> (or equivalent).</param>
/// <param name="PostprocessUs">Wall-clock microseconds spent on host-side postprocess (e.g. tensor unpacking, NMS) after <c>session.Run</c>.</param>
/// <param name="ExecutionProviderUsed">EP that actually serviced this call, e.g. <c>"DirectML"</c>, <c>"CPU"</c>.</param>
/// <param name="PeakBytesAllocated">Peak runtime-side bytes allocated for this call (best-effort; runners may report 0 if the underlying runtime exposes no peak counter).</param>
public sealed record InferenceMetrics(
    long PreprocessUs,
    long InferenceUs,
    long PostprocessUs,
    string ExecutionProviderUsed,
    long PeakBytesAllocated);
