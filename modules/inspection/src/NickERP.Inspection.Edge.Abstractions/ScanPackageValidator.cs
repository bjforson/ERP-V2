using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NickERP.Inspection.Edge.Abstractions;

/// <summary>
/// Sprint 40 / Phase A — pure validator for canonical
/// <see cref="ScanPackage"/> bundles.
///
/// <para>
/// Server-side ingest paths (e.g. EdgeReplayEndpoint manifest validation)
/// call <see cref="Validate"/> with the package + the per-edge HMAC key
/// matching the package's <see cref="ScanPackage.GatewayId"/>. The
/// validator runs three checks in this order:
/// <list type="number">
///   <item><description><b>Required-field shape.</b> ScanId is UUID-shaped;
///   ScannerId / GatewayId / SiteId / ScanType are non-empty; ImageFiles
///   has at least one entry; per-file FileName is non-empty and contains
///   no path separators; per-file Sha256Hex is 64 lowercase hex chars;
///   per-file SizeBytes matches Data.Length.</description></item>
///   <item><description><b>Per-image sha256 verification.</b> Each
///   <see cref="ImageFile"/>'s declared <see cref="ImageFile.Sha256Hex"/>
///   is recomputed against its <see cref="ImageFile.Data"/>. Any
///   mismatch fails fast — the manifest signature would also fail (the
///   manifest binds the hex), but reporting the per-file failure
///   pinpoints which image was tampered.</description></item>
///   <item><description><b>Manifest sha256 + HMAC signature.</b> The
///   manifest JSON is rebuilt from the package, sha256-hashed, and
///   compared against <see cref="ScanPackage.ManifestSha256"/>; the
///   recomputed manifest is then HMAC-signed under the supplied key
///   and compared (constant-time) against
///   <see cref="ScanPackage.ManifestSignature"/>.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Failure shape.</b> The validator returns a structured
/// <see cref="ScanPackageValidationResult"/> rather than throwing. The
/// EdgeReplayEndpoint maps the failure to a 400 with a structured error
/// indicating WHICH check failed (sha256 / signature / required-field)
/// — operators can then act on the specific failure mode.
/// </para>
/// </summary>
public static class ScanPackageValidator
{
    private static readonly Regex UuidRegex = new(
        "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled);

    private static readonly Regex Sha256HexRegex = new(
        "^[0-9a-f]{64}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Validate a <paramref name="package"/> against its expected
    /// per-edge HMAC <paramref name="hmacKey"/>. Pure function; thread
    /// safe.
    /// </summary>
    public static ScanPackageValidationResult Validate(
        ScanPackage package,
        byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(hmacKey);
        if (hmacKey.Length == 0)
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "hmac key must not be empty");
        }

        // ---- 1. Required-field shape. -----------------------------------
        if (string.IsNullOrWhiteSpace(package.ScanId) || !UuidRegex.IsMatch(package.ScanId))
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                $"scanId must be a UUID-shaped string; got '{package.ScanId}'");
        }
        if (string.IsNullOrWhiteSpace(package.ScannerId))
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "scannerId is required");
        }
        if (string.IsNullOrWhiteSpace(package.GatewayId))
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "gatewayId is required");
        }
        if (string.IsNullOrWhiteSpace(package.SiteId))
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "siteId is required");
        }
        if (string.IsNullOrWhiteSpace(package.ScanType))
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "scanType is required");
        }
        if (package.ImageFiles is null || package.ImageFiles.Count == 0)
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "at least one image file is required");
        }
        if (package.ManifestSha256 is null || package.ManifestSha256.Length != 32)
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "manifestSha256 must be a 32-byte SHA-256 digest");
        }
        if (package.ManifestSignature is null || package.ManifestSignature.Length != 32)
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.RequiredFieldMissing,
                "manifestSignature must be a 32-byte HMAC-SHA256 result");
        }

        // ---- 2. Per-image sha256 verification. --------------------------
        for (var i = 0; i < package.ImageFiles.Count; i++)
        {
            var f = package.ImageFiles[i];
            if (string.IsNullOrWhiteSpace(f.FileName))
            {
                return ScanPackageValidationResult.Failed(
                    ScanPackageFailureKind.RequiredFieldMissing,
                    $"imageFiles[{i}].fileName is required");
            }
            if (f.FileName.IndexOfAny(new[] { '/', '\\' }) >= 0)
            {
                return ScanPackageValidationResult.Failed(
                    ScanPackageFailureKind.RequiredFieldMissing,
                    $"imageFiles[{i}].fileName must not contain path separators; got '{f.FileName}'");
            }
            if (string.IsNullOrWhiteSpace(f.Sha256Hex) || !Sha256HexRegex.IsMatch(f.Sha256Hex))
            {
                return ScanPackageValidationResult.Failed(
                    ScanPackageFailureKind.RequiredFieldMissing,
                    $"imageFiles[{i}].sha256Hex must be 64 lowercase hex chars");
            }
            if (f.Data is null)
            {
                return ScanPackageValidationResult.Failed(
                    ScanPackageFailureKind.RequiredFieldMissing,
                    $"imageFiles[{i}].data is required");
            }
            if (f.SizeBytes != f.Data.Length)
            {
                return ScanPackageValidationResult.Failed(
                    ScanPackageFailureKind.ImageSha256Mismatch,
                    $"imageFiles[{i}].sizeBytes ({f.SizeBytes}) does not match data length ({f.Data.Length})");
            }
            var actualHex = ScanPackageManifest.Sha256Hex(f.Data);
            if (!string.Equals(actualHex, f.Sha256Hex, StringComparison.Ordinal))
            {
                return ScanPackageValidationResult.Failed(
                    ScanPackageFailureKind.ImageSha256Mismatch,
                    $"imageFiles[{i}].sha256Hex mismatch: declared={f.Sha256Hex}, actual={actualHex}");
            }
        }

        // ---- 3. Manifest sha256 + HMAC signature. -----------------------
        // Rebuild the manifest from the package and recompute. The
        // canonical JSON shape is byte-for-byte deterministic, so any
        // tamper to the package fields propagates to the recomputed
        // sha256 and signature.
        var rebuiltJson = ScanPackageManifest.BuildManifestJson(package);
        var rebuiltSha = ScanPackageManifest.ComputeSha256(rebuiltJson);
        if (!CryptographicOperations.FixedTimeEquals(rebuiltSha, package.ManifestSha256))
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.ManifestSha256Mismatch,
                "manifest sha256 does not match recomputed value");
        }

        var rebuiltSig = ScanPackageManifest.Sign(rebuiltJson, hmacKey);
        if (!CryptographicOperations.FixedTimeEquals(rebuiltSig, package.ManifestSignature))
        {
            return ScanPackageValidationResult.Failed(
                ScanPackageFailureKind.ManifestSignatureMismatch,
                "manifest HMAC signature does not match");
        }

        return ScanPackageValidationResult.Success(rebuiltJson);
    }
}

/// <summary>
/// Sprint 40 / Phase A — outcome of a <see cref="ScanPackageValidator.Validate"/>
/// call. <see cref="IsValid"/> distinguishes success/failure;
/// <see cref="FailureKind"/> + <see cref="FailureReason"/> are populated
/// on failure; <see cref="ManifestJson"/> is populated on success.
/// </summary>
public sealed record ScanPackageValidationResult(
    bool IsValid,
    ScanPackageFailureKind? FailureKind,
    string? FailureReason,
    byte[]? ManifestJson)
{
    /// <summary>Build a success result with the canonical manifest JSON the validator recomputed.</summary>
    public static ScanPackageValidationResult Success(byte[] manifestJson) =>
        new(true, null, null, manifestJson);

    /// <summary>Build a failure result for the indicated check.</summary>
    public static ScanPackageValidationResult Failed(ScanPackageFailureKind kind, string reason) =>
        new(false, kind, reason, null);
}

/// <summary>
/// Sprint 40 / Phase A — categorical reason a
/// <see cref="ScanPackageValidator.Validate"/> call failed. Consumers
/// surface this in audit events + structured error responses so ops
/// can drill in on tamper / corruption signals.
/// </summary>
public enum ScanPackageFailureKind
{
    /// <summary>One of the required fields was missing or malformed.</summary>
    RequiredFieldMissing = 0,

    /// <summary>An image file's recomputed sha256 disagreed with its declared digest.</summary>
    ImageSha256Mismatch = 1,

    /// <summary>The manifest's recomputed sha256 disagreed with the declared digest.</summary>
    ManifestSha256Mismatch = 2,

    /// <summary>The manifest's HMAC signature did not match (wrong key, or manifest tampered after signing).</summary>
    ManifestSignatureMismatch = 3
}
