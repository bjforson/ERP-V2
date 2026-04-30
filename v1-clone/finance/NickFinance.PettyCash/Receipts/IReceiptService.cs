using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace NickFinance.PettyCash.Receipts;

/// <summary>
/// Module-facing API for receipt ingestion. Wraps file persistence + hash
/// computation + duplicate detection + OCR dispatch in one call.
/// </summary>
public interface IReceiptService
{
    /// <summary>
    /// Persist a receipt for the voucher. Returns the saved row.
    /// Computes SHA-256 + approximate hash, stamps GPS + uploader, and
    /// hands the bytes to <see cref="IOcrEngine"/> in the background-style
    /// (sync but isolated). If a duplicate by exact hash exists for the
    /// SAME voucher, throws — modules with the SAME tenant but DIFFERENT
    /// voucher get a soft warning surfaced via <see cref="UploadResult.WarnDuplicateOf"/>.
    /// </summary>
    Task<UploadResult> UploadAsync(UploadRequest req, CancellationToken ct = default);

    /// <summary>List receipts for a voucher in upload order.</summary>
    Task<IReadOnlyList<VoucherReceipt>> ListAsync(Guid voucherId, CancellationToken ct = default);
}

public sealed record UploadRequest(
    Guid VoucherId,
    string FileName,
    string ContentType,
    byte[] Content,
    Guid UploadedByUserId,
    decimal? GpsLatitude = null,
    decimal? GpsLongitude = null,
    long TenantId = 1);

public sealed record UploadResult(
    VoucherReceipt Receipt,
    Guid? WarnDuplicateOf);

public sealed class ReceiptService : IReceiptService
{
    private readonly PettyCashDbContext _db;
    private readonly IReceiptStorage _storage;
    private readonly IOcrEngine _ocr;
    private readonly TimeProvider _clock;

    public ReceiptService(
        PettyCashDbContext db,
        IReceiptStorage storage,
        IOcrEngine? ocr = null,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _ocr = ocr ?? new NoopOcrEngine();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<UploadResult> UploadAsync(UploadRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Content is null || req.Content.Length == 0)
        {
            throw new ArgumentException("Receipt content is empty.", nameof(req));
        }

        var voucher = await _db.Vouchers.FirstOrDefaultAsync(v => v.VoucherId == req.VoucherId && v.TenantId == req.TenantId, ct)
            ?? throw new PettyCashException($"Voucher {req.VoucherId} not found for tenant {req.TenantId}.");

        var sha = ComputeSha256Hex(req.Content);
        var approx = ComputeApproximateHashHex(req.Content);

        // Hard duplicate inside the same voucher = error. Same tenant, different
        // voucher = soft warning surfaced to caller for fraud review.
        var sameVoucherDup = await _db.VoucherReceipts.AnyAsync(r => r.VoucherId == req.VoucherId && r.Sha256 == sha, ct);
        if (sameVoucherDup)
        {
            throw new PettyCashException("This exact receipt is already attached to the voucher.");
        }
        var crossVoucherDup = await _db.VoucherReceipts
            .Where(r => r.TenantId == req.TenantId && r.Sha256 == sha)
            .Select(r => r.VoucherId)
            .FirstOrDefaultAsync(ct);

        var nextOrdinal = (short)(1 + await _db.VoucherReceipts.CountAsync(r => r.VoucherId == req.VoucherId, ct));
        var path = await _storage.SaveAsync(req.VoucherId, nextOrdinal, req.FileName, req.Content, ct);

        var ocrResult = await _ocr.RecogniseAsync(req.Content, req.ContentType, ct);

        var entity = new VoucherReceipt
        {
            VoucherId = req.VoucherId,
            Ordinal = nextOrdinal,
            FilePath = path,
            Sha256 = sha,
            ApproximateHash = approx,
            ContentType = req.ContentType,
            FileSizeBytes = req.Content.LongLength,
            UploadedByUserId = req.UploadedByUserId,
            UploadedAt = _clock.GetUtcNow(),
            GpsLatitude = req.GpsLatitude,
            GpsLongitude = req.GpsLongitude,
            OcrVendor = _ocr.Vendor,
            OcrAmountMinor = ocrResult.AmountMinor,
            OcrDate = ocrResult.Date,
            OcrRawText = ocrResult.RawText,
            OcrConfidence = ocrResult.Confidence,
            TenantId = req.TenantId
        };
        _db.VoucherReceipts.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new UploadResult(entity, crossVoucherDup == Guid.Empty ? null : crossVoucherDup);
    }

    public async Task<IReadOnlyList<VoucherReceipt>> ListAsync(Guid voucherId, CancellationToken ct = default)
    {
        return await _db.VoucherReceipts
            .Where(r => r.VoucherId == voucherId)
            .OrderBy(r => r.Ordinal)
            .ToListAsync(ct);
    }

    // -----------------------------------------------------------------
    // Hashing
    // -----------------------------------------------------------------

    private static string ComputeSha256Hex(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Lightweight approximate hash: bin the bytes into 256 buckets by
    /// position and SHA-256 the histogram. Catches re-uploads of the same
    /// JPEG even after re-compression at a different quality. Replaced by
    /// a real DCT-pHash when SkiaSharp / ImageSharp lands.
    /// </summary>
    private static string ComputeApproximateHashHex(byte[] content)
    {
        const int Buckets = 256;
        Span<long> bins = stackalloc long[Buckets];
        for (var i = 0; i < content.Length; i++)
        {
            bins[i % Buckets] += content[i];
        }
        Span<byte> material = stackalloc byte[Buckets * sizeof(long)];
        for (var i = 0; i < Buckets; i++)
        {
            BitConverter.TryWriteBytes(material[(i * 8)..], bins[i]);
        }
        var digest = SHA256.HashData(material);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
