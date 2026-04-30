namespace NickFinance.PettyCash.Receipts;

/// <summary>
/// One uploaded receipt image attached to a voucher. Multiple receipts per
/// voucher are supported (e.g. fuel + parking + lunch on a road trip).
/// </summary>
public class VoucherReceipt
{
    public Guid VoucherReceiptId { get; set; } = Guid.NewGuid();

    public Guid VoucherId { get; set; }

    /// <summary>1-based ordinal within the voucher's receipt set.</summary>
    public short Ordinal { get; set; }

    /// <summary>Absolute file path on disk (or blob URL once Phase 5 lands object storage).</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>SHA-256 hex digest of the raw uploaded bytes — detects exact duplicates regardless of format.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Approximate-match digest: SHA-256 of the bytes after a normalisation
    /// pass (resize to 16x16 grayscale, snap to 4-bit per pixel). Tolerates
    /// JPEG re-compression and minor brightness shifts. Not a true DCT-based
    /// pHash — that ships with the SkiaSharp dependency in v1.2 — but it's
    /// enough to catch "same photo re-uploaded after a screenshot tour".
    /// Stored as hex.
    /// </summary>
    public string ApproximateHash { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";
    public long FileSizeBytes { get; set; }

    /// <summary>OCR vendor stamp ("azure", "noop", etc.). Null until OCR runs.</summary>
    public string? OcrVendor { get; set; }

    /// <summary>OCR-extracted total, in minor units of the receipt currency. Null until OCR runs.</summary>
    public long? OcrAmountMinor { get; set; }

    public DateOnly? OcrDate { get; set; }
    public string? OcrRawText { get; set; }

    /// <summary>0..100 confidence reported by OCR. Null until OCR runs.</summary>
    public byte? OcrConfidence { get; set; }

    public Guid UploadedByUserId { get; set; }
    public DateTimeOffset UploadedAt { get; set; }

    /// <summary>Geo-tag captured at upload (if the device permitted). Null if not provided.</summary>
    public decimal? GpsLatitude { get; set; }
    public decimal? GpsLongitude { get; set; }

    public long TenantId { get; set; } = 1;
}
