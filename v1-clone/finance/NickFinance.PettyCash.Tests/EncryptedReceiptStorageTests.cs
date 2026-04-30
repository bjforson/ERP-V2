using System.Security.Cryptography;
using System.Text;
using NickFinance.PettyCash.Receipts;
using Xunit;

namespace NickFinance.PettyCash.Tests;

/// <summary>
/// Standalone tests for <see cref="EncryptedReceiptStorage"/>. They run
/// against the in-memory inner — no DB needed — so they execute even
/// without NICKFINANCE_TEST_DB set.
/// </summary>
public class EncryptedReceiptStorageTests
{
    private static string FreshKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task RoundTrip_WritesEncryptedAndReadsPlaintext()
    {
        var inner = new InMemoryReceiptStorage();
        var enc = new EncryptedReceiptStorage(inner, FreshKey());
        var voucher = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("hello receipt — sensitive PII line goes here");

        var path = await enc.SaveAsync(voucher, ordinal: 1, fileName: "r.jpg", content: bytes);

        // Bytes on disk (in-memory inner) MUST be encrypted, not plaintext.
        var onDisk = await inner.ReadAsync(path);
        Assert.True(EncryptedReceiptStorage.LooksEncrypted(onDisk));
        Assert.NotEqual(bytes, onDisk);
        Assert.True(onDisk.Length >= EncryptedReceiptStorage.HeaderSize);

        // Reading via the wrapper returns plaintext.
        var roundTrip = await enc.ReadAsync(path);
        Assert.Equal(bytes, roundTrip);
    }

    [Fact]
    public async Task DecryptWithWrongKey_Fails()
    {
        var inner = new InMemoryReceiptStorage();
        var encA = new EncryptedReceiptStorage(inner, FreshKey());
        var encB = new EncryptedReceiptStorage(inner, FreshKey()); // different key, same inner

        var voucher = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("payload-protected-by-key-A");
        var path = await encA.SaveAsync(voucher, 1, "r.jpg", bytes);

        // Wrong-key decrypt MUST fail with a CryptographicException —
        // GCM's tag verification is the gate.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => encB.ReadAsync(path));

        // Right key still works.
        Assert.Equal(bytes, await encA.ReadAsync(path));
    }

    [Fact]
    public async Task LegacyPlaintextReceipt_PassesThroughUnencrypted()
    {
        // Simulate a receipt written before EncryptedReceiptStorage shipped:
        // raw plaintext bytes in the inner, no header. Wrapper must read
        // them transparently so the migration is non-disruptive.
        var inner = new InMemoryReceiptStorage();
        var bytes = Encoding.UTF8.GetBytes("legacy-plaintext-png-bytes-from-before-encryption-rolled-out");
        var path = await inner.SaveAsync(Guid.NewGuid(), 1, "r.jpg", bytes);

        var enc = new EncryptedReceiptStorage(inner, FreshKey());
        var read = await enc.ReadAsync(path);
        Assert.Equal(bytes, read);
        Assert.False(EncryptedReceiptStorage.LooksEncrypted(bytes));
    }

    [Fact]
    public async Task Tamper_FlippingOneByte_RaisesCryptographicException()
    {
        var inner = new InMemoryReceiptStorage();
        var enc = new EncryptedReceiptStorage(inner, FreshKey());
        var voucher = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("payload-that-must-not-be-mutated-after-write");
        var path = await enc.SaveAsync(voucher, 1, "r.jpg", bytes);

        // Mutate one byte in the ciphertext region (after the header) and
        // re-write it directly into the inner store. Reading via the
        // wrapper MUST throw — GCM authenticated encryption guarantees
        // tamper detection.
        var raw = await inner.ReadAsync(path);
        // Ciphertext starts at HeaderSize; pick a byte well inside.
        var idx = EncryptedReceiptStorage.HeaderSize + (raw.Length - EncryptedReceiptStorage.HeaderSize) / 2;
        raw[idx] ^= 0x55;
        // Stuff the mutated bytes back. InMemoryReceiptStorage keys by
        // path + Save replaces, so we re-Save with the same voucher/ordinal/
        // filename — but that would re-encrypt. Instead, mutate via a
        // direct re-save that bypasses the wrapper:
        var path2 = await inner.SaveAsync(voucher, ordinal: 2, fileName: "r2.jpg", raw);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => enc.ReadAsync(path2));
    }

    [Fact]
    public async Task TamperOnTagBytes_AlsoRaises()
    {
        var inner = new InMemoryReceiptStorage();
        var enc = new EncryptedReceiptStorage(inner, FreshKey());
        var voucher = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");
        await enc.SaveAsync(voucher, 1, "r.jpg", bytes);

        // Re-fetch inner bytes for a separate path, mutate the tag region.
        var raw = await inner.ReadAsync(await enc.SaveAsync(voucher, 5, "r5.jpg", bytes));
        // Tag region: starts at Magic.Length + NonceSize.
        var tagStart = EncryptedReceiptStorage.Magic.Length + EncryptedReceiptStorage.NonceSize;
        raw[tagStart] ^= 0xFF;
        var path2 = await inner.SaveAsync(voucher, 6, "r6.jpg", raw);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => enc.ReadAsync(path2));
    }

    [Fact]
    public void Constructor_RejectsBadBase64()
    {
        var inner = new InMemoryReceiptStorage();
        Assert.Throws<ArgumentException>(() =>
            new EncryptedReceiptStorage(inner, "this-is-not-valid-base64-!!@#$"));
    }

    [Fact]
    public void Constructor_RejectsWrongKeySize()
    {
        var inner = new InMemoryReceiptStorage();
        // 16-byte key, base64-encoded — valid base64 but wrong length.
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var ex = Assert.Throws<ArgumentException>(() =>
            new EncryptedReceiptStorage(inner, shortKey));
        Assert.Contains("32", ex.Message);
    }

    [Fact]
    public void Constructor_RejectsNullInner()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new EncryptedReceiptStorage(null!, FreshKey()));
    }

    [Fact]
    public void Constructor_RejectsEmptyKey()
    {
        var inner = new InMemoryReceiptStorage();
        Assert.Throws<ArgumentException>(() =>
            new EncryptedReceiptStorage(inner, ""));
    }

    [Fact]
    public async Task TwoSavesOfSamePlaintext_ProduceDifferentCiphertexts()
    {
        // Random nonces => different ciphertexts each save. Important
        // for both privacy (no equal-plaintext leak) and forensic
        // analysis.
        var inner = new InMemoryReceiptStorage();
        var enc = new EncryptedReceiptStorage(inner, FreshKey());
        var bytes = Encoding.UTF8.GetBytes("same-plaintext-twice");
        var p1 = await enc.SaveAsync(Guid.NewGuid(), 1, "a.jpg", bytes);
        var p2 = await enc.SaveAsync(Guid.NewGuid(), 1, "b.jpg", bytes);
        Assert.NotEqual(p1, p2);
        var c1 = await inner.ReadAsync(p1);
        var c2 = await inner.ReadAsync(p2);
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public static void LooksEncrypted_ShortBlob_ReturnsFalse()
    {
        // Anything shorter than HeaderSize bytes can't be a valid
        // encrypted blob even if it starts with the magic.
        var almost = new byte[EncryptedReceiptStorage.HeaderSize - 1];
        Array.Copy(EncryptedReceiptStorage.Magic, almost, EncryptedReceiptStorage.Magic.Length);
        Assert.False(EncryptedReceiptStorage.LooksEncrypted(almost));
    }

    [Fact]
    public static void LooksEncrypted_NoMagic_ReturnsFalse()
    {
        var bytes = Encoding.UTF8.GetBytes("PNG... actually this is JPEG, who knows");
        Assert.False(EncryptedReceiptStorage.LooksEncrypted(bytes));
    }
}
