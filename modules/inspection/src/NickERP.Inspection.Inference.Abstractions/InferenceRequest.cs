namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// One inference call against an <see cref="ILoadedModel"/>. Carries the
/// inputs (keyed by port name), an end-to-end correlation id for
/// telemetry, the resolved tenant for tenant-aware models, and an
/// optional per-call timeout.
/// </summary>
public sealed class InferenceRequest
{
    /// <summary>
    /// Input tensors keyed by the model's input port name. Caller retains
    /// ownership of the tensors until <see cref="ILoadedModel.RunAsync"/>
    /// returns; the runner does not dispose them.
    /// </summary>
    public required IReadOnlyDictionary<string, ITensor> Inputs { get; init; }

    /// <summary>End-to-end correlation id propagated to the <c>inference.run</c> span and the host's structured logs.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// Resolved tenant for this request. Tenant-keyed model variants
    /// (per the <c>models/&lt;family&gt;/&lt;version&gt;/&lt;tenant_id&gt;/model.onnx</c>
    /// convention) use this to pick the right artifact; nullable so
    /// platform-shared models can run without a tenant context.
    /// </summary>
    public int? TenantId { get; init; }

    /// <summary>Optional per-call timeout. Null = use the runner's default. Cancellation token still wins.</summary>
    public TimeSpan? Timeout { get; init; }
}
