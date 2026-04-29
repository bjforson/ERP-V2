namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Element type carried by an <see cref="ITensor"/>. Mirrors the subset of
/// ONNX <c>TensorProto.DataType</c> values used by the cargo-imaging
/// inference path. Extend (additively) when a new model needs a new dtype.
/// </summary>
public enum TensorElementType
{
    /// <summary>IEEE 754 single-precision (32-bit) floating point.</summary>
    Float32,

    /// <summary>IEEE 754 half-precision (16-bit) floating point.</summary>
    Float16,

    /// <summary>Signed 8-bit integer.</summary>
    Int8,

    /// <summary>Unsigned 8-bit integer.</summary>
    UInt8,

    /// <summary>Signed 16-bit integer.</summary>
    Int16,

    /// <summary>Signed 32-bit integer.</summary>
    Int32,

    /// <summary>Signed 64-bit integer.</summary>
    Int64,

    /// <summary>Boolean (one byte per element on most runtimes).</summary>
    Bool
}
