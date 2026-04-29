namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Runtime-agnostic tensor handle. Concrete runners wrap their native
/// tensor type (<c>OrtValue</c>, <c>DenseTensor&lt;T&gt;</c>, OpenVINO
/// <c>ov::Tensor</c>, etc.) behind this surface so the inspection core
/// and downstream postprocess code don't take a dependency on the
/// runner's runtime. The runner owns the underlying memory; calling
/// <see cref="IDisposable.Dispose"/> releases it.
/// </summary>
public interface ITensor : IDisposable
{
    /// <summary>Element type of the tensor's payload.</summary>
    TensorElementType ElementType { get; }

    /// <summary>Concrete (resolved) shape of this instance — no dynamic dimensions.</summary>
    IReadOnlyList<int> Shape { get; }

    /// <summary>Raw byte view over the tensor payload. Valid only until <see cref="IDisposable.Dispose"/>.</summary>
    Span<byte> AsBytes();

    /// <summary>Typed view over the tensor payload. <typeparamref name="T"/> must match <see cref="ElementType"/>; mismatches are caller error.</summary>
    Span<T> AsSpan<T>() where T : unmanaged;
}
