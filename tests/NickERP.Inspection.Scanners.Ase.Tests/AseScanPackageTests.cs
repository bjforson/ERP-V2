using System.Text;
using NickERP.Inspection.Edge.Abstractions;
using NickERP.Inspection.Scanners.Abstractions;
using NickERP.Inspection.Scanners.Ase;

namespace NickERP.Inspection.Scanners.Ase.Tests;

/// <summary>
/// Sprint 50 / Phase A — ASE adapter contract conformance. Asserts the
/// stub plugin satisfies the same contract surface FS6000 ships under so
/// the host (worker, registry, validator) treats both adapters
/// uniformly.
///
/// <para>
/// Mirrors the Sprint 45 <c>FS6000ScanPackageTests</c> shape: build →
/// sign → verify round-trip + cursor-sync stub behavior. Vendor-specific
/// real-protocol wiring lands later — these tests guard the contract
/// invariants the worker depends on.
/// </para>
/// </summary>
public sealed class AseScanPackageTests
{
    private static readonly byte[] HmacKey =
        Encoding.UTF8.GetBytes("test-edge-hmac-key-stable-32bytes");

    private static readonly ScannerCapabilities DualEnergy = new(
        SupportedFormats: new[] { "image/png", "vendor/ase" },
        SupportedModes: new[] { "high-energy", "low-energy" },
        SupportsLiveStream: false,
        SupportsDualEnergy: true);

    private static readonly ScannerCapabilities SingleEnergy = new(
        SupportedFormats: new[] { "image/png" },
        SupportedModes: new[] { "primary" },
        SupportsLiveStream: false,
        SupportsDualEnergy: false);

    /// <summary>
    /// Mirror the host's post-parse fill of SiteId / GatewayId — adapter
    /// leaves them empty so the host can pull them from the resolved
    /// ScannerDeviceInstance + edge-node context. Validator requires
    /// them; tests fill here.
    /// </summary>
    private static ScanPackage HostFill(ScanPackage pkg) => pkg with
    {
        SiteId = "TMA",
        GatewayId = "edge-tema-1"
    };

    [Fact]
    public void TypeCode_MatchesPluginAttribute()
    {
        var adapter = new AseScannerAdapter();
        adapter.TypeCode.Should().Be("ase",
            because: "the IPluginRegistry resolves adapters by lower-case TypeCode");
    }

    [Fact]
    public void Capabilities_DeclareCursorSyncShape()
    {
        var adapter = new AseScannerAdapter();
        adapter.Capabilities.SupportsLiveStream.Should().BeFalse(
            because: "ASE is a cursor-pull source, not a stream");
        adapter.Capabilities.SupportsDualEnergy.Should().BeTrue(
            because: "ASE upstream surfaces high+low energy channels");
        adapter.Capabilities.SupportedFormats.Should().Contain("vendor/ase");
    }

    [Fact]
    public void Implements_BothScannerAndCursorSyncContracts()
    {
        var adapter = new AseScannerAdapter();
        // The worker resolves IScannerAdapter then downcasts to
        // IScannerCursorSyncAdapter — guard both halves.
        (adapter is IScannerAdapter).Should().BeTrue();
        (adapter is IScannerCursorSyncAdapter).Should().BeTrue();
    }

    [Fact]
    public async Task TestAsync_NoConnectionString_ReturnsIdleSuccess()
    {
        var adapter = new AseScannerAdapter();
        var cfg = new ScannerDeviceConfig(
            DeviceId: Guid.NewGuid(),
            LocationId: Guid.NewGuid(),
            StationId: null,
            TenantId: 1,
            ConfigJson: "{}");

        var result = await adapter.TestAsync(cfg);

        result.Success.Should().BeTrue(
            because: "the health-sweep worker should not flag stub-mode adapters as unreachable");
        result.Message.Should().Contain("idle",
            because: "operators need to know the adapter is intentionally quiet");
    }

    [Fact]
    public async Task TestAsync_BadConfigJson_StillSurfacesSuccess()
    {
        // Defensive: malformed config shouldn't throw — the adapter
        // tolerates and falls back to default config (= empty connection
        // string, idle).
        var adapter = new AseScannerAdapter();
        var cfg = new ScannerDeviceConfig(
            DeviceId: Guid.NewGuid(),
            LocationId: Guid.NewGuid(),
            StationId: null,
            TenantId: 1,
            ConfigJson: "{not-valid-json");

        var result = await adapter.TestAsync(cfg);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task StreamAsync_ReturnsEmpty()
    {
        var adapter = new AseScannerAdapter();
        var cfg = new ScannerDeviceConfig(
            DeviceId: Guid.NewGuid(),
            LocationId: Guid.NewGuid(),
            StationId: null,
            TenantId: 1,
            ConfigJson: "{}");

        var collected = new List<RawScanArtifact>();
        await foreach (var raw in adapter.StreamAsync(cfg))
        {
            collected.Add(raw);
        }

        collected.Should().BeEmpty(
            because: "ASE is a cursor-pull source; streaming path must no-op cleanly");
    }

    [Fact]
    public async Task ParseScanAsync_ProducesValidBundleAfterSeal()
    {
        var adapter = new AseScannerAdapter();
        var rawBytes = Encoding.UTF8.GetBytes("ase-row-payload-bytes");

        var parsed = await adapter.ParseScanAsync(rawBytes, DualEnergy);

        parsed.Should().NotBeNull();
        parsed.Package.Should().NotBeNull();
        parsed.Package.ScannerId.Should().Be("ase");
        parsed.Package.ScanType.Should().Be("primary");
        parsed.Package.ImageFiles.Should().HaveCount(1);
        parsed.Package.ImageFiles[0].Data.Should().BeEquivalentTo(rawBytes);
        parsed.Package.ImageFiles[0].Sha256Hex.Should().HaveLength(64);
        parsed.Package.ImageFiles[0].SizeBytes.Should().Be(rawBytes.LongLength);
        parsed.Package.ManifestSha256.Should().BeEmpty(
            because: "the adapter intentionally ships unsigned packages — host seals");
        parsed.Package.ManifestSignature.Should().BeEmpty();

        var sealed_ = ScanPackageManifest.Seal(HostFill(parsed.Package), HmacKey);
        sealed_.ManifestSha256.Should().HaveCount(32);
        sealed_.ManifestSignature.Should().HaveCount(32);

        var verdict = ScanPackageValidator.Validate(sealed_, HmacKey);
        verdict.IsValid.Should().BeTrue(
            because: "the adapter's bundle must round-trip through the same validator FS6000 uses");
        verdict.FailureKind.Should().BeNull();
    }

    [Fact]
    public async Task ParseScanAsync_DualEnergyCapability_TagsView()
    {
        var adapter = new AseScannerAdapter();
        var parsed = await adapter.ParseScanAsync(new byte[] { 1, 2, 3 }, DualEnergy);
        parsed.Package.ImageFiles[0].View.Should().Be("high-energy");
    }

    [Fact]
    public async Task ParseScanAsync_SingleEnergyCapability_TagsPrimary()
    {
        var adapter = new AseScannerAdapter();
        var parsed = await adapter.ParseScanAsync(new byte[] { 1, 2, 3 }, SingleEnergy);
        parsed.Package.ImageFiles[0].View.Should().Be("primary");
    }

    [Fact]
    public async Task ParseScanAsync_TamperedImageBytes_FailsValidation()
    {
        var adapter = new AseScannerAdapter();
        var rawBytes = Encoding.UTF8.GetBytes("ase-row-payload-bytes");
        var parsed = await adapter.ParseScanAsync(rawBytes, DualEnergy);

        var sealed_ = ScanPackageManifest.Seal(HostFill(parsed.Package), HmacKey);

        // Flip a byte AFTER sealing so the manifest sha256 of the
        // recomputed image disagrees with the manifest's recorded hash.
        // Validator MUST catch this independently of the manifest signature.
        var tamperedImage = sealed_.ImageFiles[0] with
        {
            Data = TamperFirstByte(sealed_.ImageFiles[0].Data)
        };
        var tamperedPkg = sealed_ with { ImageFiles = new[] { tamperedImage } };

        var verdict = ScanPackageValidator.Validate(tamperedPkg, HmacKey);
        verdict.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task PullAsync_StubMode_ReturnsEmptyBatchAndKeepsCursor()
    {
        var adapter = new AseScannerAdapter();
        var cfg = new ScannerDeviceConfig(
            DeviceId: Guid.NewGuid(),
            LocationId: Guid.NewGuid(),
            StationId: null,
            TenantId: 1,
            ConfigJson: "{}");

        var batch = await adapter.PullAsync(cfg, cursor: "row-42", batchLimit: 100, ct: CancellationToken.None);

        batch.Records.Should().BeEmpty();
        batch.HasMore.Should().BeFalse();
        batch.NextCursor.Should().Be("row-42",
            because: "stub mode must not advance the cursor — workers replaying with the same cursor get the same (empty) result");
    }

    [Fact]
    public async Task PullAsync_FirstCycle_AcceptsEmptyCursor()
    {
        var adapter = new AseScannerAdapter();
        var cfg = new ScannerDeviceConfig(
            DeviceId: Guid.NewGuid(),
            LocationId: Guid.NewGuid(),
            StationId: null,
            TenantId: 1,
            ConfigJson: "{}");

        // Worker passes string.Empty on the first cycle ever — adapter
        // must tolerate without throwing.
        var batch = await adapter.PullAsync(cfg, cursor: string.Empty, batchLimit: 100, ct: CancellationToken.None);
        batch.NextCursor.Should().Be(string.Empty);
    }

    [Fact]
    public async Task PullAsync_NegativeBatchLimit_Throws()
    {
        var adapter = new AseScannerAdapter();
        var cfg = new ScannerDeviceConfig(
            DeviceId: Guid.NewGuid(),
            LocationId: Guid.NewGuid(),
            StationId: null,
            TenantId: 1,
            ConfigJson: "{}");

        await FluentActions
            .Invoking(() => adapter.PullAsync(cfg, cursor: string.Empty, batchLimit: -1, ct: CancellationToken.None))
            .Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ComputeIdempotencyKey_IsStableAcrossInvocations()
    {
        var k1 = AseScannerAdapter.ComputeIdempotencyKey("row-1|2026-05-05T10:00:00Z");
        var k2 = AseScannerAdapter.ComputeIdempotencyKey("row-1|2026-05-05T10:00:00Z");
        k1.Should().Be(k2);
        k1.Should().HaveLength(64,
            because: "SHA-256 hex digest is 32 bytes -> 64 hex chars");
    }

    [Fact]
    public void ComputeIdempotencyKey_DifferentInputs_ProduceDifferentKeys()
    {
        var k1 = AseScannerAdapter.ComputeIdempotencyKey("row-1");
        var k2 = AseScannerAdapter.ComputeIdempotencyKey("row-2");
        k1.Should().NotBe(k2);
    }

    private static byte[] TamperFirstByte(byte[] source)
    {
        var copy = (byte[])source.Clone();
        copy[0] ^= 0xFF;
        return copy;
    }
}
