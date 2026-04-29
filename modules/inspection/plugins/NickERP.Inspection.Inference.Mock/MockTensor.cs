using System.Runtime.InteropServices;
using NickERP.Inspection.Inference.Abstractions;

namespace NickERP.Inspection.Inference.Mock;

/// <summary>
/// Heap-allocated <see cref="ITensor"/> over a managed byte array. Used
/// both as the inference output by <see cref="MockLoadedModel"/> and as
/// a fixture vehicle for test inputs the host wants to pin to a specific
/// output. <see cref="Dispose"/> is a no-op (managed memory).
/// </summary>
public sealed class MockTensor : ITensor
{
    private readonly byte[] _bytes;
    private readonly int[] _shape;
    private bool _disposed;

    /// <summary>Wrap an explicit byte payload + shape + element type.</summary>
    public MockTensor(byte[] bytes, IReadOnlyList<int> shape, TensorElementType elementType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(shape);
        _bytes = bytes;
        _shape = shape.ToArray();
        ElementType = elementType;
    }

    /// <summary>Allocate a zero-initialised tensor of the given shape and element type.</summary>
    public static MockTensor Allocate(IReadOnlyList<int> shape, TensorElementType elementType)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var elementSize = ElementSize(elementType);
        var count = 1L;
        foreach (var d in shape) count *= Math.Max(1, d);
        return new MockTensor(new byte[count * elementSize], shape, elementType);
    }

    /// <summary>Allocate a tensor and fill it deterministically from <paramref name="seed"/>.</summary>
    public static MockTensor Random(IReadOnlyList<int> shape, TensorElementType elementType, long seed)
    {
        var t = Allocate(shape, elementType);
        var rng = new Random(unchecked((int)(seed ^ (seed >> 32))));
        rng.NextBytes(t._bytes);
        return t;
    }

    /// <inheritdoc />
    public TensorElementType ElementType { get; }

    /// <inheritdoc />
    public IReadOnlyList<int> Shape
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(MockTensor));
            return _shape;
        }
    }

    /// <inheritdoc />
    public Span<byte> AsBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(MockTensor));
        return _bytes;
    }

    /// <inheritdoc />
    public Span<T> AsSpan<T>() where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(MockTensor));
        return MemoryMarshal.Cast<byte, T>(_bytes.AsSpan());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Managed memory — nothing native to release; mark disposed for
        // contract symmetry with OnnxTensor.
        _disposed = true;
    }

    /// <summary>Bytes per element for each supported <see cref="TensorElementType"/>.</summary>
    public static int ElementSize(TensorElementType type) => type switch
    {
        TensorElementType.Float32 => 4,
        TensorElementType.Float16 => 2,
        TensorElementType.Int8 => 1,
        TensorElementType.UInt8 => 1,
        TensorElementType.Int16 => 2,
        TensorElementType.Int32 => 4,
        TensorElementType.Int64 => 8,
        TensorElementType.Bool => 1,
        _ => throw new NotSupportedException($"Unknown element type {type}.")
    };
}
