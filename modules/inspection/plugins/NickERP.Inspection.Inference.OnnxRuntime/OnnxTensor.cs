using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using NickERP.Inspection.Inference.Abstractions;
using Float16 = Microsoft.ML.OnnxRuntime.Float16;
using OrtTensorElementType = Microsoft.ML.OnnxRuntime.Tensors.TensorElementType;

namespace NickERP.Inspection.Inference.OnnxRuntime;

/// <summary>
/// <see cref="ITensor"/> over an ONNX Runtime <see cref="OrtValue"/>. Owns
/// the underlying <c>OrtValue</c>; <see cref="Dispose"/> releases it. The
/// tensor surfaces <see cref="ITensor.AsBytes"/> and <see cref="ITensor.AsSpan{T}"/>
/// against the <c>OrtValue</c>'s pinned native memory — valid only until
/// disposal.
/// </summary>
public sealed class OnnxTensor : ITensor
{
    private readonly OrtValue _value;
    private readonly TensorElementType _elementType;
    private readonly int[] _shape;
    private bool _disposed;

    /// <summary>Wrap an existing <see cref="OrtValue"/>. The <see cref="OnnxTensor"/> takes ownership and disposes it.</summary>
    public OnnxTensor(OrtValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;

        var typeShape = value.GetTensorTypeAndShape();
        _elementType = MapElementType((OrtTensorElementType)typeShape.ElementDataType);

        var shapeLong = typeShape.Shape;
        _shape = new int[shapeLong.Length];
        for (var i = 0; i < shapeLong.Length; i++)
        {
            // Concrete shapes only; dynamic dims must be resolved before
            // a tensor is materialised.
            checked { _shape[i] = (int)shapeLong[i]; }
        }
    }

    /// <inheritdoc />
    public TensorElementType ElementType
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(OnnxTensor));
            return _elementType;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<int> Shape
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(OnnxTensor));
            return _shape;
        }
    }

    /// <summary>Underlying OrtValue. Exposed so the runner can pass it directly into the next session call when chaining models.</summary>
    public OrtValue OrtValue
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(OnnxTensor));
            return _value;
        }
    }

    /// <inheritdoc />
    public Span<byte> AsBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(OnnxTensor));
        // OrtValue exposes a typed Span; reinterpret to bytes via MemoryMarshal.
        return _elementType switch
        {
            TensorElementType.Float32 => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<float>()),
            TensorElementType.Float16 => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<Float16>()),
            TensorElementType.Int8 => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<sbyte>()),
            TensorElementType.UInt8 => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<byte>()),
            TensorElementType.Int16 => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<short>()),
            TensorElementType.Int32 => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<int>()),
            TensorElementType.Int64 => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<long>()),
            TensorElementType.Bool => MemoryMarshal.AsBytes(_value.GetTensorMutableDataAsSpan<bool>()),
            _ => throw new NotSupportedException($"Unsupported element type {_elementType}.")
        };
    }

    /// <inheritdoc />
    public Span<T> AsSpan<T>() where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, typeof(OnnxTensor));
        return _value.GetTensorMutableDataAsSpan<T>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _value.Dispose();
        _disposed = true;
    }

    /// <summary>Translate an ONNX tensor element type to the runtime-agnostic enum.</summary>
    internal static TensorElementType MapElementType(OrtTensorElementType ortType) => ortType switch
    {
        OrtTensorElementType.Float => TensorElementType.Float32,
        OrtTensorElementType.Float16 => TensorElementType.Float16,
        OrtTensorElementType.Int8 => TensorElementType.Int8,
        OrtTensorElementType.UInt8 => TensorElementType.UInt8,
        OrtTensorElementType.Int16 => TensorElementType.Int16,
        OrtTensorElementType.Int32 => TensorElementType.Int32,
        OrtTensorElementType.Int64 => TensorElementType.Int64,
        OrtTensorElementType.Bool => TensorElementType.Bool,
        _ => throw new NotSupportedException($"ONNX tensor element type {ortType} not supported by NickERP.Inspection.Inference.")
    };

    /// <summary>Translate a runtime-agnostic enum back to the ONNX one.</summary>
    internal static OrtTensorElementType MapElementType(TensorElementType type) => type switch
    {
        TensorElementType.Float32 => OrtTensorElementType.Float,
        TensorElementType.Float16 => OrtTensorElementType.Float16,
        TensorElementType.Int8 => OrtTensorElementType.Int8,
        TensorElementType.UInt8 => OrtTensorElementType.UInt8,
        TensorElementType.Int16 => OrtTensorElementType.Int16,
        TensorElementType.Int32 => OrtTensorElementType.Int32,
        TensorElementType.Int64 => OrtTensorElementType.Int64,
        TensorElementType.Bool => OrtTensorElementType.Bool,
        _ => throw new NotSupportedException($"Element type {type} has no ONNX mapping.")
    };
}
