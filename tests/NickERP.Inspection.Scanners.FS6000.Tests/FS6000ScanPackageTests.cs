using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NickERP.Inspection.Edge.Abstractions;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Inspection.Scanners.FS6000;

namespace NickERP.Inspection.Scanners.FS6000.Tests;

/// <summary>
/// Sprint 45 / Phase E — coverage for the FS6000 plugin's canonical-shape
/// adoption + the underlying ScanPackage validation contract.
///
/// <para>
/// Asserts:
/// <list type="bullet">
///   <item><description>ScanPackage build → sign → verify happy path round-trip via the FS6000 plugin.</description></item>
///   <item><description>Per-image sha256 tamper detection — flipping a byte inside the image data fails verification independently of the manifest signature.</description></item>
///   <item><description>Manifest tamper detection — flipping a byte after sign fails the HMAC verification.</description></item>
///   <item><description>FS6000 single-blob input path produces a valid bundle (degenerate fallback).</description></item>
///   <item><description>FS6000 manifest-envelope input path produces a multi-channel bundle.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class FS6000ScanPackageTests
{
    private static readonly byte[] HmacKey =
        Encoding.UTF8.GetBytes("test-edge-hmac-key-stable-32bytes");

    private static readonly ScannerCapabilities Capabilities = new(
        SupportedFormats: new[] { "image/png" },
        SupportedModes: new[] { "high-energy", "low-energy", "material" },
        SupportsLiveStream: true,
        SupportsDualEnergy: true);

    /// <summary>
    /// Mirror the host's post-parse fill of SiteId / GatewayId — the
    /// adapter intentionally leaves them empty so the host can pull
    /// them from the resolved ScannerDeviceInstance + edge-node
    /// context. The validator requires them; tests fill them here.
    /// </summary>
    private static ScanPackage HostFill(ScanPackage pkg) => pkg with
    {
        SiteId = "TKD",
        GatewayId = "edge-test"
    };

    [Fact]
    public async Task ParseScanAsync_SingleBlob_ProducesValidBundleAfterSeal()
    {
        var adapter = new FS6000ScannerAdapter();
        var rawBytes = BuildRawImg(64, seed: 0x42);

        var parsed = await adapter.ParseScanAsync(rawBytes, Capabilities);

        parsed.Should().NotBeNull();
        parsed.Package.Should().NotBeNull();
        parsed.Package.ScannerId.Should().Be("fs6000");
        parsed.Package.ImageFiles.Should().HaveCount(1);
        parsed.Package.ImageFiles[0].Data.Should().BeEquivalentTo(rawBytes);
        parsed.Package.ManifestSha256.Should().BeEmpty();
        parsed.Package.ManifestSignature.Should().BeEmpty();

        // The plugin emits the canonical bundle with empty SiteId /
        // GatewayId — the host fills them post-parse from the
        // resolved ScannerDeviceInstance + edge-node context. Populate
        // here for the validator's required-field check.
        var hostFilled = parsed.Package with
        {
            SiteId = "TKD",
            GatewayId = "edge-tema-1"
        };

        var sealed_ = ScanPackageManifest.Seal(hostFilled, HmacKey);
        sealed_.ManifestSha256.Should().HaveCount(32);
        sealed_.ManifestSignature.Should().HaveCount(32);

        var verdict = ScanPackageValidator.Validate(sealed_, HmacKey);
        verdict.IsValid.Should().BeTrue();
        verdict.FailureKind.Should().BeNull();
    }

    [Fact]
    public async Task ParseScanAsync_ManifestEnvelope_ProducesThreeChannelBundle()
    {
        var stem = Path.Combine(Path.GetTempPath(), "fs6000-test-" + Guid.NewGuid().ToString("N"));
        var highPath = stem + "high.img";
        var lowPath = stem + "low.img";
        var materialPath = stem + "material.img";
        try
        {
            await File.WriteAllBytesAsync(highPath, BuildRawImg(64, seed: 0x10));
            await File.WriteAllBytesAsync(lowPath, BuildRawImg(64, seed: 0x20));
            await File.WriteAllBytesAsync(materialPath, BuildRawImg(64, seed: 0x30));

            var manifestBytes = BuildManifestEnvelope(
                stem: stem, highPath: highPath, lowPath: lowPath,
                materialPath: materialPath);

            var adapter = new FS6000ScannerAdapter();
            var parsed = await adapter.ParseScanAsync(manifestBytes, Capabilities);

            parsed.Package.ImageFiles.Should().HaveCount(3);
            parsed.Package.ImageFiles.Select(f => f.View).Should().BeEquivalentTo(
                new[] { "high-energy", "low-energy", "material" });
            parsed.Package.ImageFiles.Should().AllSatisfy(f =>
            {
                f.Sha256Hex.Should().HaveLength(64);
                f.Data.Should().NotBeNullOrEmpty();
                f.SizeBytes.Should().Be(f.Data.Length);
            });
            parsed.Package.ScannerId.Should().Be("fs6000");
            parsed.Package.ScanType.Should().Be("primary");

            // Round-trip via Seal + Validate.
            var sealed_ = ScanPackageManifest.Seal(HostFill(parsed.Package), HmacKey);
            var verdict = ScanPackageValidator.Validate(sealed_, HmacKey);
            verdict.IsValid.Should().BeTrue();
        }
        finally
        {
            TryDelete(highPath);
            TryDelete(lowPath);
            TryDelete(materialPath);
        }
    }

    [Fact]
    public async Task ParseScanAsync_ManifestEnvelope_WithoutMaterial_ProducesTwoChannelBundle()
    {
        var stem = Path.Combine(Path.GetTempPath(), "fs6000-test-" + Guid.NewGuid().ToString("N"));
        var highPath = stem + "high.img";
        var lowPath = stem + "low.img";
        try
        {
            await File.WriteAllBytesAsync(highPath, BuildRawImg(64, seed: 0x40));
            await File.WriteAllBytesAsync(lowPath, BuildRawImg(64, seed: 0x50));

            var manifestBytes = BuildManifestEnvelope(
                stem: stem, highPath: highPath, lowPath: lowPath, materialPath: null);

            var adapter = new FS6000ScannerAdapter();
            var parsed = await adapter.ParseScanAsync(manifestBytes, Capabilities);

            parsed.Package.ImageFiles.Should().HaveCount(2);
            parsed.Package.ImageFiles.Select(f => f.View).Should().BeEquivalentTo(
                new[] { "high-energy", "low-energy" });
        }
        finally
        {
            TryDelete(highPath);
            TryDelete(lowPath);
        }
    }

    [Fact]
    public async Task Validate_ManifestTamperedAfterSeal_FailsManifestSha256Check()
    {
        var adapter = new FS6000ScannerAdapter();
        var parsed = await adapter.ParseScanAsync(BuildRawImg(32, 0x10), Capabilities);
        var sealed_ = ScanPackageManifest.Seal(HostFill(parsed.Package), HmacKey);

        // Mutate a high-level field after seal — the manifest sha256
        // recompute will see the new value, mismatching the stored
        // ManifestSha256 byte digest.
        var tampered = sealed_ with { ContainerNumber = "TAMPERED-CONTAINER" };

        var verdict = ScanPackageValidator.Validate(tampered, HmacKey);
        verdict.IsValid.Should().BeFalse();
        verdict.FailureKind.Should().Be(ScanPackageFailureKind.ManifestSha256Mismatch);
    }

    [Fact]
    public async Task Validate_ImageBytesTampered_FailsImageSha256Check()
    {
        var adapter = new FS6000ScannerAdapter();
        var rawBytes = BuildRawImg(32, 0x10);
        var parsed = await adapter.ParseScanAsync(rawBytes, Capabilities);
        var sealed_ = ScanPackageManifest.Seal(HostFill(parsed.Package), HmacKey);

        // Flip one byte inside the image data; declared Sha256Hex
        // stays the same so the recompute will mismatch.
        var tamperedData = (byte[])sealed_.ImageFiles[0].Data.Clone();
        tamperedData[5] ^= 0xFF;
        var tamperedFile = sealed_.ImageFiles[0] with { Data = tamperedData };
        var tampered = sealed_ with { ImageFiles = new[] { tamperedFile } };

        var verdict = ScanPackageValidator.Validate(tampered, HmacKey);
        verdict.IsValid.Should().BeFalse();
        verdict.FailureKind.Should().Be(ScanPackageFailureKind.ImageSha256Mismatch);
    }

    [Fact]
    public async Task Validate_WrongHmacKey_FailsSignatureCheck()
    {
        var adapter = new FS6000ScannerAdapter();
        var parsed = await adapter.ParseScanAsync(BuildRawImg(32, 0x60), Capabilities);
        var sealed_ = ScanPackageManifest.Seal(HostFill(parsed.Package), HmacKey);

        var wrongKey = Encoding.UTF8.GetBytes("different-edge-key-32bytes-fixed");
        var verdict = ScanPackageValidator.Validate(sealed_, wrongKey);

        verdict.IsValid.Should().BeFalse();
        verdict.FailureKind.Should().Be(ScanPackageFailureKind.ManifestSignatureMismatch);
    }

    [Fact]
    public void Validate_EmptyImageList_FailsRequiredField()
    {
        var package = new ScanPackage(
            ScanId: Guid.NewGuid().ToString(),
            SiteId: "site",
            ScannerId: "fs6000",
            GatewayId: "gw",
            ScanType: "primary",
            OccurredAt: DateTimeOffset.UtcNow,
            OperatorId: string.Empty,
            ContainerNumber: string.Empty,
            VehiclePlate: string.Empty,
            DeclarationNumber: string.Empty,
            ManifestNumber: string.Empty,
            ImageFiles: Array.Empty<ImageFile>(),
            ManifestSha256: new byte[32],
            ManifestSignature: new byte[32]);

        var verdict = ScanPackageValidator.Validate(package, HmacKey);
        verdict.IsValid.Should().BeFalse();
        verdict.FailureKind.Should().Be(ScanPackageFailureKind.RequiredFieldMissing);
    }

    [Fact]
    public void Validate_PathSeparatorInFileName_FailsRequiredField()
    {
        var data = new byte[16];
        var hex = ScanPackageManifest.Sha256Hex(data);
        var package = new ScanPackage(
            ScanId: Guid.NewGuid().ToString(),
            SiteId: "site",
            ScannerId: "fs6000",
            GatewayId: "gw",
            ScanType: "primary",
            OccurredAt: DateTimeOffset.UtcNow,
            OperatorId: string.Empty,
            ContainerNumber: string.Empty,
            VehiclePlate: string.Empty,
            DeclarationNumber: string.Empty,
            ManifestNumber: string.Empty,
            ImageFiles: new[] { new ImageFile("dir/foo.img", hex, "primary", data.Length, data) },
            ManifestSha256: new byte[32],
            ManifestSignature: new byte[32]);

        var verdict = ScanPackageValidator.Validate(package, HmacKey);
        verdict.IsValid.Should().BeFalse();
        verdict.FailureKind.Should().Be(ScanPackageFailureKind.RequiredFieldMissing);
        verdict.FailureReason.Should().Contain("path separator");
    }

    [Fact]
    public async Task ParseScanAsync_SingleBlob_FileNameHasNoPathSeparators()
    {
        var adapter = new FS6000ScannerAdapter();
        var parsed = await adapter.ParseScanAsync(BuildRawImg(32, 0x77), Capabilities);

        parsed.Package.ImageFiles.Should().AllSatisfy(f =>
        {
            f.FileName.Should().NotBeNullOrEmpty();
            f.FileName.Should().NotContain("/");
            f.FileName.Should().NotContain("\\");
        });
    }

    [Fact]
    public async Task ParseScanAsync_PerImageSha256_MatchesDataDigest()
    {
        var adapter = new FS6000ScannerAdapter();
        var rawBytes = BuildRawImg(64, 0xA5);
        var parsed = await adapter.ParseScanAsync(rawBytes, Capabilities);

        var imageFile = parsed.Package.ImageFiles[0];
        var actualHex = Convert.ToHexString(SHA256.HashData(imageFile.Data)).ToLowerInvariant();
        imageFile.Sha256Hex.Should().Be(actualHex);
    }

    /// <summary>
    /// Build the FS6000 stream-emitted manifest envelope JSON. Mirrors
    /// the shape <c>FS6000ScannerAdapter.BuildRawArtifact</c> serialises.
    /// </summary>
    private static byte[] BuildManifestEnvelope(
        string stem, string highPath, string lowPath, string? materialPath)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"Stem\":").Append(JsonString(stem)).Append(',');
        sb.Append("\"HighPath\":").Append(JsonString(highPath)).Append(',');
        sb.Append("\"HighSha256\":").Append(JsonString(HashHex(highPath))).Append(',');
        sb.Append("\"LowPath\":").Append(JsonString(lowPath)).Append(',');
        sb.Append("\"LowSha256\":").Append(JsonString(HashHex(lowPath))).Append(',');
        if (materialPath is not null)
        {
            sb.Append("\"MaterialPath\":").Append(JsonString(materialPath)).Append(',');
            sb.Append("\"MaterialSha256\":").Append(JsonString(HashHex(materialPath))).Append(',');
        }
        else
        {
            sb.Append("\"MaterialPath\":null,");
            sb.Append("\"MaterialSha256\":null,");
        }
        sb.Append("\"PreviewPercentileLow\":null,");
        sb.Append("\"PreviewPercentileHigh\":null");
        sb.Append('}');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string JsonString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string HashHex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    /// Build a synthetic FS6000-shaped raw .img blob — a tiny header
    /// plus a deterministic ramp body. Same shape FS6000FormatDecoder
    /// understands (height/width/timestamp prefix).
    /// </summary>
    private static byte[] BuildRawImg(int payloadSize, byte seed)
    {
        // 22-byte header (matches FS6000FormatDecoder header layout) +
        // payload. Use deterministic values so tests are reproducible.
        const int headerSize = 22;
        var bytes = new byte[headerSize + payloadSize];
        // Width = 8, Height = payloadSize / 8 / 2 (16-bit), arbitrary
        // timestamp. The decoder is best-effort here — validation is
        // about sha256, not decode correctness.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0, 2), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 4);
        for (var i = headerSize; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + i);
        }
        return bytes;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
