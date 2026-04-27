using System.Buffers.Binary;
using System.Security.Cryptography;
using NickERP.Inspection.Scanners.FS6000;

namespace NickERP.Inspection.Scanners.FS6000.Tests;

/// <summary>
/// Byte-pattern parity test for the FS6000 channel decoder.
///
/// We synthesize three channel blobs with known headers + ramp payloads
/// (high BE u16, low BE u16, material u8), feed them to
/// <see cref="FS6000FormatDecoder.Decode"/>, and assert SHA-256 of every
/// output buffer plus the parsed dimensions / timestamp. Synthesizing
/// the input is fine — the goal is regression detection: any future tweak
/// to byte-swap, vertical-flip, or row-stride math will desync these
/// checksums and fail the test.
/// </summary>
public sealed class FS6000FormatDecoderTests
{
    private const ushort Width = 4;
    private const ushort Height = 3;

    // Recorded constants — frozen from the first green run after the
    // header + payload synthesis below. Updating any of these requires
    // a deliberate, reviewed change to the decoder. The constants assume
    // little-endian x64 (CI / dev runners): the ushort[] is hashed in
    // its native-endian byte representation.
    private const string ExpectedHighSha256 =
        "02512505c5fd3f0184cca9bda7078d0ef9e7b1431841799a20eff4dee43cf48f";
    private const string ExpectedLowSha256 =
        "e6346ec1a2c330cd01ceca4ddeeeae4c84470e10e09bc5026cadc5b4255534f6";
    private const string ExpectedMaterialSha256 =
        "4535e7adb5f11beec92136928e41ae24e57e76bec587538bb90bb03893affa24";

    [Fact]
    public void Decode_ThreeChannelKnownInput_MatchesByteParity()
    {
        // Regression guarded: silent drift in FS6000 decoder math
        // (byte-swap, vertical flip, row stride) versus the v1 reference port.
        byte[] high = BuildChannel(bitDepth: 16, fillSeed: 0x10);
        byte[] low = BuildChannel(bitDepth: 16, fillSeed: 0x40);
        byte[] material = BuildChannel(bitDepth: 8, fillSeed: 0x07);

        var decoded = FS6000FormatDecoder.Decode(high, low, material);

        decoded.Width.Should().Be(Width);
        decoded.Height.Should().Be(Height);
        decoded.High.Should().HaveCount(Width * Height);
        decoded.Low.Should().HaveCount(Width * Height);
        decoded.Material.Should().HaveCount(Width * Height);

        decoded.Timestamp.Should().Be(new DateTime(2026, 4, 26, 11, 22, 33, DateTimeKind.Unspecified));

        // Byte-parity assertions: hash the native-endian output buffers.
        Sha256OfUshortArray(decoded.High).Should().Be(ExpectedHighSha256);
        Sha256OfUshortArray(decoded.Low).Should().Be(ExpectedLowSha256);
        Sha256OfBytes(decoded.Material).Should().Be(ExpectedMaterialSha256);
    }

    /// <summary>
    /// Build one synthetic FS6000 channel blob: 36-byte header + a ramp payload.
    /// 16-bit channels get a u16 ramp (BE on disk, native after decode);
    /// 8-bit channels get a u8 ramp.
    /// </summary>
    internal static byte[] BuildChannel(int bitDepth, byte fillSeed)
    {
        int payloadBytes = Width * Height * (bitDepth / 8);
        var blob = new byte[FS6000FormatDecoder.HeaderSize + payloadBytes];

        // Header — every multi-byte value is big-endian.
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(0, 2), 0x0064);          // magic
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(2, 2), Width);
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(4, 2), Height);
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(10, 2), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(14, 2), (ushort)bitDepth);
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(16, 2), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(24, 2), 2026); // year
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(26, 2), 4);    // month
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(28, 2), 26);   // day
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(30, 2), 11);   // hour
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(32, 2), 22);   // minute
        BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(34, 2), 33);   // second

        // Payload — predictable ramp, written big-endian for u16 channels.
        var payload = blob.AsSpan(FS6000FormatDecoder.HeaderSize);
        if (bitDepth == 16)
        {
            for (int row = 0; row < Height; row++)
            {
                for (int col = 0; col < Width; col++)
                {
                    ushort v = (ushort)(fillSeed * 256 + row * 16 + col);
                    BinaryPrimitives.WriteUInt16BigEndian(
                        payload.Slice((row * Width + col) * 2, 2), v);
                }
            }
        }
        else
        {
            for (int row = 0; row < Height; row++)
            {
                for (int col = 0; col < Width; col++)
                {
                    payload[row * Width + col] = (byte)(fillSeed + row * Width + col);
                }
            }
        }

        return blob;
    }

    internal static string Sha256OfUshortArray(ushort[] data)
    {
        // Hash the native-endian byte representation of the ushort array.
        // Hash is platform-dependent in theory but the CI / dev hosts here
        // are little-endian x64 — frozen constants assume that.
        var bytes = new byte[data.Length * 2];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string Sha256OfBytes(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
