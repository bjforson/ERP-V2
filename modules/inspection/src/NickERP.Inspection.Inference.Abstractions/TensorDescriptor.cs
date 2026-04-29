namespace NickERP.Inspection.Inference.Abstractions;

/// <summary>
/// Static description of one model input or output port — name, dtype,
/// declared shape (with <see langword="null"/> entries marking dynamic
/// dimensions, e.g. <c>[null, 3, 224, 224]</c> for a dynamic batch).
/// Lives on <see cref="ModelMetadata"/>.
/// </summary>
/// <param name="Name">Port name (e.g. <c>input.1</c>, <c>logits</c>).</param>
/// <param name="ElementType">Declared element type.</param>
/// <param name="Shape">Declared shape; <see langword="null"/> entries are dynamic dimensions resolved per-request.</param>
public sealed record TensorDescriptor(
    string Name,
    TensorElementType ElementType,
    IReadOnlyList<int?> Shape);
