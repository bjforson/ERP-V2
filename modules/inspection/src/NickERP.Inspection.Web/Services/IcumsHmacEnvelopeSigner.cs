using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.ExternalSystems.Abstractions;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 9 / FU-icums-signing — HMAC-SHA256 implementation of
/// <see cref="IIcumsEnvelopeSigner"/>.
///
/// <para>
/// Key material is stored on
/// <see cref="IcumsSigningKey.KeyMaterialEncrypted"/> wrapped via
/// ASP.NET Core data protection (purpose
/// <see cref="DataProtectionPurpose"/>) and only decrypted in-process
/// at sign / verify time. The DB row alone is not enough to forge a
/// signature; an attacker would also need the host's data-protection
/// key ring.
/// </para>
///
/// <para>
/// <b>Header format.</b>
/// <c>icums-hmac-sha256 keyId=&lt;k&gt; sig=&lt;base64-of-hmac&gt;</c>.
/// Parser is strict — extra whitespace inside tokens, missing keys, or
/// algorithm-name drift all surface as a failure reason rather than a
/// silent acceptance of garbage.
/// </para>
/// </summary>
public sealed class IcumsHmacEnvelopeSigner : IIcumsEnvelopeSigner
{
    /// <summary>
    /// Data-protection purpose string. Stable on disk; rotating means
    /// a (rare) follow-up migration to re-wrap every row.
    /// </summary>
    public const string DataProtectionPurpose = "icums-signing-keys-v1";

    /// <summary>Algorithm token in the signature header.</summary>
    public const string Algorithm = "icums-hmac-sha256";

    /// <summary>Number of bytes of HMAC key material new keys are issued with.</summary>
    public const int KeyMaterialLengthBytes = 32;

    private readonly InspectionDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<IcumsHmacEnvelopeSigner> _logger;

    public IcumsHmacEnvelopeSigner(
        InspectionDbContext db,
        IDataProtectionProvider dataProtection,
        ILogger<IcumsHmacEnvelopeSigner> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        ArgumentNullException.ThrowIfNull(dataProtection);
        _protector = dataProtection.CreateProtector(DataProtectionPurpose);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<SignedEnvelope> SignAsync(string tenantId, ReadOnlyMemory<byte> envelopePayload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var tenant = ParseTenantId(tenantId);

        // Active = activated + not retired. RLS filters on app.tenant_id
        // so the WHERE on TenantId is defence-in-depth (tenant_id session
        // variable is the actual gate). We still pass it explicitly to
        // make the intent obvious in the SQL plan.
        var active = await _db.IcumsSigningKeys.AsNoTracking()
            .Where(k => k.TenantId == tenant
                        && k.ActivatedAt != null
                        && k.RetiredAt == null)
            .OrderByDescending(k => k.ActivatedAt)
            .FirstOrDefaultAsync(ct);

        if (active is null)
        {
            throw new InvalidOperationException(
                $"No active IcumsGh signing key for tenant {tenant}. Issue + activate a key via the rotation admin endpoints.");
        }

        var keyBytes = UnwrapKey(active.KeyMaterialEncrypted);
        try
        {
            var sig = ComputeHmac(keyBytes, envelopePayload.Span);
            var header = $"{Algorithm} keyId={active.KeyId} sig={Convert.ToBase64String(sig)}";
            return new SignedEnvelope(envelopePayload.ToArray(), header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <inheritdoc />
    public async Task<SignatureVerification> VerifyAsync(string tenantId, ReadOnlyMemory<byte> envelopePayload, string signatureHeader, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrWhiteSpace(signatureHeader))
            return new SignatureVerification(false, null, "Empty signature header.");

        if (!TryParseHeader(signatureHeader, out var keyId, out var sigBytes, out var parseError))
            return new SignatureVerification(false, null, parseError);

        var tenant = ParseTenantId(tenantId);
        var now = DateTimeOffset.UtcNow;

        // Pull the row by (tenant, keyId). RLS narrows to the caller's
        // tenant; the explicit Where keeps the SQL plan tight.
        var row = await _db.IcumsSigningKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.TenantId == tenant && k.KeyId == keyId, ct);

        if (row is null)
            return new SignatureVerification(false, null, $"Unknown keyId '{keyId}' for tenant.");

        if (row.ActivatedAt is null)
            return new SignatureVerification(false, keyId, $"Key '{keyId}' was never activated.");

        // Acceptance window:
        //   active for signing → RetiredAt is null
        //   verification-only window → RetiredAt set, VerificationOnlyUntil > now
        //   fully retired → reject
        if (row.RetiredAt is not null)
        {
            if (row.VerificationOnlyUntil is null || row.VerificationOnlyUntil <= now)
            {
                return new SignatureVerification(false, keyId, $"Key '{keyId}' is retired and outside its verification window.");
            }
        }

        var keyBytes = UnwrapKey(row.KeyMaterialEncrypted);
        try
        {
            var expected = ComputeHmac(keyBytes, envelopePayload.Span);
            // Constant-time compare to keep timing-side-channels closed.
            if (!CryptographicOperations.FixedTimeEquals(expected, sigBytes))
            {
                return new SignatureVerification(false, keyId, "Signature mismatch.");
            }
            return new SignatureVerification(true, keyId, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <summary>
    /// Wrap raw HMAC key material via data protection, returning the
    /// ciphertext bytes to persist on
    /// <see cref="IcumsSigningKey.KeyMaterialEncrypted"/>. Public so the
    /// rotation service can use it without depending on
    /// <see cref="IDataProtectionProvider"/> directly.
    /// </summary>
    public byte[] WrapKeyForStorage(ReadOnlySpan<byte> rawKey)
    {
        if (rawKey.Length == 0)
            throw new ArgumentException("Raw key cannot be empty.", nameof(rawKey));
        return _protector.Protect(rawKey.ToArray());
    }

    /// <summary>
    /// Generate fresh HMAC key material from a CSPRNG. Caller is
    /// responsible for wrapping (<see cref="WrapKeyForStorage"/>) and
    /// persisting; the raw bytes returned here MUST NOT land on disk.
    /// </summary>
    public static byte[] GenerateKeyMaterial()
    {
        return RandomNumberGenerator.GetBytes(KeyMaterialLengthBytes);
    }

    // -- helpers ---------------------------------------------------------

    private byte[] UnwrapKey(byte[] ciphertext)
    {
        try
        {
            return _protector.Unprotect(ciphertext);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to unwrap IcumsGh signing key (data-protection key ring may have rotated).");
            throw;
        }
    }

    private static byte[] ComputeHmac(byte[] key, ReadOnlySpan<byte> payload)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(payload.ToArray());
    }

    private static long ParseTenantId(string tenantId)
    {
        if (!long.TryParse(tenantId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) || t <= 0)
            throw new ArgumentException($"Tenant id must be a positive integer; got '{tenantId}'.", nameof(tenantId));
        return t;
    }

    /// <summary>
    /// Parse the signature header. Strict: format must be
    /// <c>icums-hmac-sha256 keyId=&lt;k&gt; sig=&lt;base64&gt;</c> with
    /// exactly those tokens, in that order, separated by single spaces.
    /// Extra whitespace, reordered tokens, or different algorithms all
    /// fail with a descriptive reason. Returns true on success.
    /// </summary>
    internal static bool TryParseHeader(
        string header,
        out string keyId,
        out byte[] sigBytes,
        out string failureReason)
    {
        keyId = string.Empty;
        sigBytes = Array.Empty<byte>();
        failureReason = string.Empty;

        var parts = header.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            failureReason = $"Expected 3 space-separated tokens, got {parts.Length}.";
            return false;
        }

        if (!string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            failureReason = $"Unsupported algorithm '{parts[0]}'.";
            return false;
        }

        const string keyIdPrefix = "keyId=";
        if (!parts[1].StartsWith(keyIdPrefix, StringComparison.Ordinal))
        {
            failureReason = "Missing keyId= token.";
            return false;
        }
        keyId = parts[1][keyIdPrefix.Length..];
        if (string.IsNullOrEmpty(keyId))
        {
            failureReason = "Empty keyId.";
            return false;
        }

        const string sigPrefix = "sig=";
        if (!parts[2].StartsWith(sigPrefix, StringComparison.Ordinal))
        {
            failureReason = "Missing sig= token.";
            return false;
        }
        var sigB64 = parts[2][sigPrefix.Length..];
        try
        {
            sigBytes = Convert.FromBase64String(sigB64);
        }
        catch (FormatException)
        {
            failureReason = "sig= value is not valid base64.";
            return false;
        }

        return true;
    }
}
