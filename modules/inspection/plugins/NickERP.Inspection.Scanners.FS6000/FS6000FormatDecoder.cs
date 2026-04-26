using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NickERP.Inspection.Scanners.FS6000;

/// <summary>
/// Pure-C# parser for FS6000 scanner raw channel <c>.img</c> blobs.
/// Ported point-in-time from NSCIM v1
/// (<c>NickScanCentralImagingPortal.Services.ImageProcessing.FS6000.FS6000FormatDecoder</c>)
/// — the algorithm is the canonical reverse-engineering of the vendor format
/// and was already covered by integration tests against real scanner output.
/// No code share with v1; this is a clean copy in the v2 namespace.
///
/// Format:
///
/// <code>
///   offset  size  field                    notes
///   ------  ----  ---------------------    ---------------------------------
///   0       2     ?? (0x0064 = 100)
///   2       2     width   u16 BE
///   4       2     height  u16 BE
///   6       2     reserved
///   8       2     reserved
///   10      2     0xFFFF
///   12      2     0x0000
///   14      2     bitDepth u16 BE         16 for high/low, 8 for material
///   16      2     0x0001
///   18..23        zeros
///   24      2     year   u16 BE
///   26      2     month  u16 BE
///   28      2     day    u16 BE
///   30      2     hour   u16 BE
///   32      2     minute u16 BE
///   34      2     second u16 BE
///   36..          pixel data, big-endian; width * height * (bit_depth/8) bytes
/// </code>
///
/// An FS6000 scan is three sibling .img blobs — <c>high</c> (16-bit),
/// <c>low</c> (16-bit), and <c>material</c> (8-bit) — that share identical
/// width/height and align pixel-for-pixel.
///
/// Orientation note: the scanner delivers data with row 0 at the top of the
/// container; the vendor display convention is bottom-at-top (truck driving
/// rightward with wheels along the lower edge). This decoder applies the
/// vertical flip so the returned buffer matches the vendor JPEG orientation
/// exactly — callers never need to flip again.
///
/// Endianness note: FS6000 is big-endian throughout. This implementation
/// targets x64 and uses
/// <see cref="BinaryPrimitives.ReverseEndianness(System.ReadOnlySpan{ushort}, System.Span{ushort})"/>
/// for the byte-swap pass, which vectorizes on modern CPUs.
/// </summary>
public static class FS6000FormatDecoder
{
    public const int HeaderSize = 36;

    private const int OffsetWidth = 2;
    private const int OffsetHeight = 4;
    private const int OffsetBitDepth = 14;
    private const int OffsetYear = 24;
    private const int OffsetMonth = 26;
    private const int OffsetDay = 28;
    private const int OffsetHour = 30;
    private const int OffsetMinute = 32;
    private const int OffsetSecond = 34;

    // Upper bound for allocations from a corrupt header. Real FS6000 scans
    // are 2295 x 1378; 16384 x 16384 gives us 64x headroom before we bail
    // out, which is far more than the vendor hardware can produce.
    private const int MaxDimension = 16384;

    /// <summary>
    /// Parsed 36-byte channel header. Dimensions are shared across all
    /// three channels of a scan; <see cref="BitDepth"/> differs
    /// (16 for energies, 8 for material).
    /// </summary>
    public readonly struct Fs6000Header
    {
        public readonly ushort Width;
        public readonly ushort Height;
        public readonly ushort BitDepth;
        public readonly DateTime? Timestamp;

        public Fs6000Header(ushort width, ushort height, ushort bitDepth, DateTime? timestamp)
        {
            Width = width;
            Height = height;
            BitDepth = bitDepth;
            Timestamp = timestamp;
        }

        /// <summary>Parse the first <see cref="HeaderSize"/> bytes of a channel blob.</summary>
        public static Fs6000Header Parse(byte[] data)
        {
            if (data == null)
                throw new InvalidDataException("FS6000 channel data is null");
            if (data.Length < HeaderSize)
                throw new InvalidDataException(
                    $"FS6000 header too short: {data.Length} bytes (need at least {HeaderSize})");

            ushort width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetWidth, 2));
            ushort height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetHeight, 2));
            ushort bitDepth = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetBitDepth, 2));

            if (bitDepth != 8 && bitDepth != 16)
                throw new InvalidDataException($"FS6000 unexpected bit depth: {bitDepth}");
            if (width == 0 || height == 0)
                throw new InvalidDataException($"FS6000 invalid dimensions: {width}x{height}");
            if (width > MaxDimension || height > MaxDimension)
                throw new InvalidDataException(
                    $"FS6000 dimensions out of supported range: {width}x{height} (max {MaxDimension}x{MaxDimension})");

            DateTime? timestamp = null;
            ushort year = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetYear, 2));
            if (year >= 2000 && year <= 2100)
            {
                ushort month = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetMonth, 2));
                ushort day = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetDay, 2));
                ushort hour = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetHour, 2));
                ushort minute = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetMinute, 2));
                ushort second = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(OffsetSecond, 2));
                try
                {
                    timestamp = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
                }
                catch (ArgumentOutOfRangeException)
                {
                    timestamp = null;
                }
            }

            return new Fs6000Header(width, height, bitDepth, timestamp);
        }
    }

    /// <summary>
    /// Result of a successful three-channel decode. All three pixel buffers
    /// are row-major, fully native-endian, and already vertically flipped
    /// to vendor orientation.
    /// </summary>
    public readonly struct DecodedFs6000
    {
        public readonly int Width;
        public readonly int Height;
        /// <summary>High-energy channel, length = Width*Height.</summary>
        public readonly ushort[] High;
        /// <summary>Low-energy channel, length = Width*Height.</summary>
        public readonly ushort[] Low;
        /// <summary>Material classification channel (8-bit class indices), length = Width*Height. May be empty for partial-channel scans.</summary>
        public readonly byte[] Material;
        public readonly DateTime? Timestamp;

        public DecodedFs6000(int width, int height, ushort[] high, ushort[] low, byte[] material, DateTime? timestamp)
        {
            Width = width;
            Height = height;
            High = high;
            Low = low;
            Material = material;
            Timestamp = timestamp;
        }
    }

    /// <summary>Decode a full FS6000 scan from its three raw channel byte buffers.</summary>
    public static DecodedFs6000 Decode(byte[] highBytes, byte[] lowBytes, byte[] materialBytes)
    {
        var hHdr = Fs6000Header.Parse(highBytes);
        var lHdr = Fs6000Header.Parse(lowBytes);
        var mHdr = Fs6000Header.Parse(materialBytes);

        if (hHdr.Width != lHdr.Width || hHdr.Height != lHdr.Height)
            throw new InvalidDataException(
                $"FS6000 high/low dimension mismatch: {hHdr.Width}x{hHdr.Height} vs {lHdr.Width}x{lHdr.Height}");
        if (hHdr.Width != mHdr.Width || hHdr.Height != mHdr.Height)
            throw new InvalidDataException(
                $"FS6000 high/material dimension mismatch: {hHdr.Width}x{hHdr.Height} vs {mHdr.Width}x{mHdr.Height}");
        if (hHdr.BitDepth != 16 || lHdr.BitDepth != 16)
            throw new InvalidDataException(
                $"FS6000 expected 16-bit for high/low, got {hHdr.BitDepth}/{lHdr.BitDepth}");
        if (mHdr.BitDepth != 8)
            throw new InvalidDataException(
                $"FS6000 expected 8-bit for material, got {mHdr.BitDepth}");

        int w = hHdr.Width;
        int h = hHdr.Height;

        ushort[] high = DecodeChannel16BitFlipped(highBytes, w, h);
        ushort[] low = DecodeChannel16BitFlipped(lowBytes, w, h);
        byte[] material = DecodeChannel8BitFlipped(materialBytes, w, h);

        return new DecodedFs6000(w, h, high, low, material, hHdr.Timestamp);
    }

    /// <summary>
    /// Partial-channel variant. Decodes only HE + LE when the scanner didn't
    /// produce a material.img for this scan.
    /// </summary>
    public static (int Width, int Height, ushort[] High, ushort[] Low, DateTime? Timestamp)
        DecodeEnergyOnly(byte[] highBytes, byte[] lowBytes)
    {
        var hHdr = Fs6000Header.Parse(highBytes);
        var lHdr = Fs6000Header.Parse(lowBytes);

        if (hHdr.Width != lHdr.Width || hHdr.Height != lHdr.Height)
            throw new InvalidDataException(
                $"FS6000 high/low dimension mismatch: {hHdr.Width}x{hHdr.Height} vs {lHdr.Width}x{lHdr.Height}");
        if (hHdr.BitDepth != 16 || lHdr.BitDepth != 16)
            throw new InvalidDataException(
                $"FS6000 expected 16-bit for high/low, got {hHdr.BitDepth}/{lHdr.BitDepth}");

        int w = hHdr.Width;
        int h = hHdr.Height;
        ushort[] high = DecodeChannel16BitFlipped(highBytes, w, h);
        ushort[] low = DecodeChannel16BitFlipped(lowBytes, w, h);
        return (w, h, high, low, hHdr.Timestamp);
    }

    /// <summary>
    /// Decode a single 16-bit channel (big-endian payload, vertically flipped).
    /// Returns a native-endian <see cref="ushort"/>[] of length <c>width*height</c>.
    /// </summary>
    private static ushort[] DecodeChannel16BitFlipped(byte[] data, int width, int height)
    {
        long pixelCount = (long)width * height;
        long pixelBytes = pixelCount * 2;
        long required = HeaderSize + pixelBytes;
        if (data.Length < required)
            throw new InvalidDataException(
                $"FS6000 16-bit channel truncated: {data.Length} bytes, need {required} " +
                $"for declared {width}x{height}");

        var pixels = new ushort[pixelCount];

        var srcBytes = data.AsSpan(HeaderSize, (int)pixelBytes);
        var srcU16 = MemoryMarshal.Cast<byte, ushort>(srcBytes);
        var dstU16 = pixels.AsSpan();
        for (int r = 0; r < height; r++)
        {
            int srcRowStart = r * width;
            int dstRowStart = (height - 1 - r) * width;
            srcU16.Slice(srcRowStart, width).CopyTo(dstU16.Slice(dstRowStart, width));
        }

        BinaryPrimitives.ReverseEndianness(pixels.AsSpan(), pixels.AsSpan());

        return pixels;
    }

    /// <summary>Decode a single 8-bit channel (vertically flipped).</summary>
    private static byte[] DecodeChannel8BitFlipped(byte[] data, int width, int height)
    {
        long pixelCount = (long)width * height;
        long required = HeaderSize + pixelCount;
        if (data.Length < required)
            throw new InvalidDataException(
                $"FS6000 8-bit channel truncated: {data.Length} bytes, need {required} " +
                $"for declared {width}x{height}");

        var pixels = new byte[pixelCount];
        for (int r = 0; r < height; r++)
        {
            int srcRowStart = HeaderSize + r * width;
            int dstRowStart = (height - 1 - r) * width;
            Buffer.BlockCopy(data, srcRowStart, pixels, dstRowStart, width);
        }
        return pixels;
    }
}
