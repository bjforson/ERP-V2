using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NickERP.Inspection.Edge.Abstractions;

/// <summary>
/// Sprint 40 / Phase A — pure helpers for building, hashing, and signing
/// the canonical <see cref="ScanPackage"/> manifest JSON.
///
/// <para>
/// <b>Canonical JSON shape.</b> The manifest is serialised with
/// deterministic key ordering and no whitespace so two edges producing
/// the same logical bundle hash to the same bytes regardless of platform
/// JSON quirks. Key order is fixed by <see cref="WriteCanonicalJson"/>;
/// inside <see cref="ImageFile"/> arrays the file order is preserved
/// from the input list (the producer is the source of truth for file
/// ordering — typically primary-then-side-then-aux).
/// </para>
///
/// <para>
/// <b>Why HMAC the manifest, not the bytes.</b> Per Sprint 40
/// architectural constraints, signing the JSON manifest (which already
/// includes each file's sha256 hex digest + size) is sufficient for
/// chain-of-custody: any tampering with file content is caught when the
/// validator recomputes the sha256 against the data. Signing every
/// image byte directly would multiply the edge's CPU cost on hot scan
/// paths without strengthening the security posture. This pattern
/// matches industry conventions (e.g. JWS detached payload, sigstore
/// transparency-log entries).
/// </para>
///
/// <para>
/// <b>HMAC key reuse.</b> The per-edge HMAC key issued by Sprint 13 T2
/// <c>EdgeAuthHandler</c> doubles as the signing key here — no new key
/// infrastructure. The server-side validator looks the key up by
/// <see cref="ScanPackage.GatewayId"/> + verifies the same HMAC.
/// </para>
/// </summary>
public static class ScanPackageManifest
{
    /// <summary>
    /// Build the canonical manifest JSON for a <paramref name="package"/>.
    /// Deterministic byte-for-byte: same logical input → same output.
    /// The result is suitable for sha256 hashing and HMAC signing.
    ///
    /// <para>
    /// <b>Note.</b> The <see cref="ScanPackage.ManifestSha256"/> and
    /// <see cref="ScanPackage.ManifestSignature"/> fields on the input
    /// are deliberately NOT included in the manifest body — they are
    /// the OUTPUT of this function and including them would create a
    /// chicken-and-egg dependency. The validator passes a "manifest
    /// candidate" with empty signature fields when recomputing.
    /// </para>
    /// </summary>
    public static byte[] BuildManifestJson(ScanPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonicalJson(writer, package);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Compute the sha256 digest of an arbitrary byte sequence — typically
    /// the manifest JSON returned by <see cref="BuildManifestJson"/>.
    /// </summary>
    public static byte[] ComputeSha256(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SHA256.HashData(payload);
    }

    /// <summary>
    /// HMAC-SHA256 sign <paramref name="manifestJson"/> under
    /// <paramref name="hmacKey"/>. Returns the 32-byte signature. The
    /// caller stores it on
    /// <see cref="ScanPackage.ManifestSignature"/>.
    /// </summary>
    public static byte[] Sign(byte[] manifestJson, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        ArgumentNullException.ThrowIfNull(hmacKey);
        if (hmacKey.Length == 0)
            throw new ArgumentException("HMAC key must not be empty.", nameof(hmacKey));
        using var hmac = new HMACSHA256(hmacKey);
        return hmac.ComputeHash(manifestJson);
    }

    /// <summary>
    /// Convenience: build the manifest, sha256 hash, and HMAC sign in one
    /// shot. Returns a "sealed" copy of <paramref name="package"/> with
    /// <see cref="ScanPackage.ManifestSha256"/> and
    /// <see cref="ScanPackage.ManifestSignature"/> populated.
    /// </summary>
    public static ScanPackage Seal(ScanPackage package, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(package);
        var json = BuildManifestJson(package);
        var sha = ComputeSha256(json);
        var sig = Sign(json, hmacKey);
        return package with
        {
            ManifestSha256 = sha,
            ManifestSignature = sig
        };
    }

    /// <summary>
    /// Lowercase-hex-encode raw bytes — used in the JSON manifest for
    /// the per-file sha256 strings.
    /// </summary>
    public static string ToHexLower(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compute the lowercase-hex sha256 of arbitrary bytes — used by
    /// adapters when populating <see cref="ImageFile.Sha256Hex"/>.
    /// </summary>
    public static string Sha256Hex(byte[] data)
    {
        return ToHexLower(SHA256.HashData(data));
    }

    // ---- Canonical JSON shape ------------------------------------------
    // Keys emitted in this exact order, matching the doc §9 reference
    // shape: scan_id, site_id, scanner_id, gateway_id, scan_type,
    // occurred_at, operator_id, container_number, vehicle_plate,
    // declaration_number, manifest_number, image_files (each with
    // file_name, sha256, view, size_bytes — file_name first for human
    // readability when debugging).
    private static void WriteCanonicalJson(Utf8JsonWriter writer, ScanPackage package)
    {
        writer.WriteStartObject();
        writer.WriteString("scan_id", package.ScanId);
        writer.WriteString("site_id", package.SiteId);
        writer.WriteString("scanner_id", package.ScannerId);
        writer.WriteString("gateway_id", package.GatewayId);
        writer.WriteString("scan_type", package.ScanType);
        writer.WriteString("occurred_at", package.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("operator_id", package.OperatorId ?? string.Empty);
        writer.WriteString("container_number", package.ContainerNumber ?? string.Empty);
        writer.WriteString("vehicle_plate", package.VehiclePlate ?? string.Empty);
        writer.WriteString("declaration_number", package.DeclarationNumber ?? string.Empty);
        writer.WriteString("manifest_number", package.ManifestNumber ?? string.Empty);
        writer.WritePropertyName("image_files");
        writer.WriteStartArray();
        foreach (var f in package.ImageFiles ?? Array.Empty<ImageFile>())
        {
            writer.WriteStartObject();
            writer.WriteString("file_name", f.FileName);
            writer.WriteString("sha256", f.Sha256Hex);
            writer.WriteString("view", f.View ?? string.Empty);
            writer.WriteNumber("size_bytes", f.SizeBytes);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Decode a UTF-8 manifest JSON byte array — convenience for tests
    /// and diagnostics. Production code shouldn't need to parse the
    /// manifest back; the validator works on the raw bytes.
    /// </summary>
    public static string DecodeUtf8(byte[] manifestJson)
    {
        ArgumentNullException.ThrowIfNull(manifestJson);
        return Encoding.UTF8.GetString(manifestJson);
    }
}
