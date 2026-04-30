namespace NickFinance.PettyCash.Receipts;

/// <summary>
/// Pluggable OCR backend. Production wiring uses
/// <c>AzureFormRecognizerOcrEngine</c> (added separately when the
/// AZURE_DOCUMENT_INTELLIGENCE_KEY is configured); tests use
/// <see cref="NoopOcrEngine"/> which returns a fixed result.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Vendor identifier, persisted on the receipt for audit.</summary>
    string Vendor { get; }

    /// <summary>Run OCR on the bytes. Implementations should handle JPEG/PNG/PDF.</summary>
    Task<OcrResult> RecogniseAsync(byte[] content, string contentType, CancellationToken ct = default);
}

/// <summary>The shape every OCR engine returns. Confidence is 0..100.</summary>
public sealed record OcrResult(
    long? AmountMinor,
    DateOnly? Date,
    string? RawText,
    byte? Confidence);

/// <summary>
/// No-op engine — returns a result with all fields null and confidence 0.
/// Used as the default before any vendor is configured. Petty Cash still
/// works without OCR; the engine just doesn't auto-fill receipt amount.
/// </summary>
public sealed class NoopOcrEngine : IOcrEngine
{
    public string Vendor => "noop";

    public Task<OcrResult> RecogniseAsync(byte[] content, string contentType, CancellationToken ct = default)
        => Task.FromResult(new OcrResult(null, null, null, 0));
}
