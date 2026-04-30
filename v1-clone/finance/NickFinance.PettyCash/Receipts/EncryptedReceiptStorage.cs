using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NickFinance.PettyCash.Receipts;

/// <summary>
/// AES-256-GCM at-rest encryption wrapper around an inner
/// <see cref="IReceiptStorage"/>. Receipts on disk are unreadable to
/// anyone who can read the bytes (incl. local Administrators, backup
/// processes, and ransomware staging) without the symmetric DEK held in
/// machine env var <c>NICKFINANCE_RECEIPT_DEK</c>.
/// </summary>
/// <remarks>
/// <para>
/// On-disk format (single contiguous blob; no per-receipt files
/// created beyond what the inner storage already does):
/// </para>
/// <code>
/// +---------+--------+-------+---------------------+
/// |  magic  | nonce  |  tag  | ciphertext (rest)   |
/// |  8 byte | 12 byte| 16 b  | (size = plaintext)  |
/// +---------+--------+-------+---------------------+
/// magic = ASCII "NEPENC1\0" (NickFinance Encrypted Petty cash ENC v1)
/// </code>
/// <para>
/// The magic header lets <see cref="ReadAsync"/> distinguish encrypted
/// blobs from legacy plaintext receipts written before this rollout and
/// transparently pass plaintext through during the migration window. New
/// writes are always encrypted when the wrapper is in the chain.
/// </para>
/// <para>
/// Tamper detection is built in: GCM verifies the tag on read and
/// throws <see cref="CryptographicException"/> on any single-bit
/// mutation of nonce, tag, or ciphertext.
/// </para>
/// </remarks>
public sealed class EncryptedReceiptStorage : IReceiptStorage
{
    /// <summary>8-byte ASCII header — match before considering a blob encrypted.</summary>
    public static readonly byte[] Magic = "NEPENC1\0"u8.ToArray();

    public const int NonceSize = 12;  // AES-GCM standard nonce size
    public const int TagSize = 16;    // AES-GCM standard tag size
    public const int HeaderSize = 8 + NonceSize + TagSize; // 36 bytes total framing overhead

    private readonly IReceiptStorage _inner;
    private readonly byte[] _key;

    /// <summary>
    /// Wrap <paramref name="inner"/> with AES-256-GCM using the
    /// base64-encoded 32-byte key in <paramref name="base64Key"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Key isn't valid base64 or isn't exactly 32 bytes.</exception>
    public EncryptedReceiptStorage(IReceiptStorage inner, string base64Key)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Key);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("NICKFINANCE_RECEIPT_DEK must be a valid base64 string.", nameof(base64Key), ex);
        }
        if (key.Length != 32)
        {
            throw new ArgumentException(
                $"NICKFINANCE_RECEIPT_DEK must decode to 32 bytes (AES-256). Got {key.Length}.",
                nameof(base64Key));
        }
        _inner = inner;
        _key = key;
    }

    /// <inheritdoc />
    public Task<string> SaveAsync(Guid voucherId, short ordinal, string fileName, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var encrypted = Encrypt(content);
        return _inner.SaveAsync(voucherId, ordinal, fileName, encrypted, ct);
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        var raw = await _inner.ReadAsync(path, ct).ConfigureAwait(false);
        if (!LooksEncrypted(raw))
        {
            // Legacy plaintext receipt written before encryption was
            // enabled. Pass through so the migration can be lazy —
            // operators run a one-time re-encrypt pass at their
            // convenience; in the meantime old receipts stay readable.
            return raw;
        }
        return Decrypt(raw);
    }

    /// <summary>
    /// Returns true if <paramref name="blob"/> begins with the
    /// <see cref="Magic"/> header AND is at least
    /// <see cref="HeaderSize"/> bytes long.
    /// </summary>
    public static bool LooksEncrypted(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize) return false;
        return blob[..Magic.Length].SequenceEqual(Magic);
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        // Random 96-bit nonce per receipt. Birthday collision is
        // ~2^-48 at 2^24 receipts — well past every plausible scale of
        // this app — and the failure mode is "nonce reuse against the
        // same key", not catastrophic key recovery. Acceptable.
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var output = new byte[HeaderSize + plaintext.Length];
        var span = output.AsSpan();

        Magic.CopyTo(span);
        nonce.CopyTo(span.Slice(Magic.Length));
        var tagSpan = span.Slice(Magic.Length + NonceSize, TagSize);
        var ctSpan = span.Slice(HeaderSize);

        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, plaintext, ctSpan, tagSpan);
        return output;
    }

    private byte[] Decrypt(byte[] blob)
    {
        // Caller has already verified LooksEncrypted, so the slices below are safe.
        var nonce = blob.AsSpan(Magic.Length, NonceSize);
        var tag = blob.AsSpan(Magic.Length + NonceSize, TagSize);
        var ct = blob.AsSpan(HeaderSize);
        var plaintext = new byte[ct.Length];

        using var gcm = new AesGcm(_key, TagSize);
        // CryptographicException on any tag mismatch — bubbles up to the
        // caller; ReceiptService logs + 500s. We do NOT want to silently
        // return a damaged blob.
        gcm.Decrypt(nonce, ct, tag, plaintext);
        return plaintext;
    }
}
