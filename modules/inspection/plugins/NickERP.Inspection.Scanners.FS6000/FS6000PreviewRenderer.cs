using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace NickERP.Inspection.Scanners.FS6000;

/// <summary>
/// Renders an FS6000 16-bit channel down to an 8-bit grayscale PNG suitable
/// as a Blazor-side preview / list thumbnail.
///
/// The full analyst viewer (the v1 W/L sliders, 16-bit client-side decode,
/// pixel probe) belongs in the pre-render service per ROADMAP §4.3 — not in
/// the plugin. The plugin's job is just to surface a reasonable
/// out-of-the-box preview the host can stash on the ScanArtifact and the
/// Cases list can show.
///
/// Rendering pipeline:
///   1. Sample the 16-bit channel and find percentile clip points (defaults
///      1% / 99%) — robust against outliers from beam attenuation extremes.
///   2. Linearly remap each pixel to [0, 255] within those clip points.
///   3. Encode as a single-channel PNG (no alpha, no color profile).
///
/// This is a minimal stand-in for v1's <c>FS6000ChannelRenderer</c> — we drop
/// the gamma and the multi-mode catalog because they're analyst-display
/// concerns that should run server-side or client-side in the viewer, not
/// inside an ingestion plugin.
/// </summary>
internal static class FS6000PreviewRenderer
{
    public static byte[] RenderHighEnergyPng(
        ushort[] highEnergy,
        int width,
        int height,
        double percentileLow,
        double percentileHigh)
    {
        if (highEnergy.Length != (long)width * height)
            throw new ArgumentException(
                $"Pixel buffer length {highEnergy.Length} doesn't match {width}x{height}={(long)width * height}.",
                nameof(highEnergy));
        if (percentileLow < 0 || percentileLow >= percentileHigh || percentileHigh > 100)
            throw new ArgumentException(
                $"Bad percentile clip: low={percentileLow} high={percentileHigh}");

        var (lo, hi) = PercentileClipPoints(highEnergy, percentileLow, percentileHigh);
        if (hi <= lo) hi = (ushort)Math.Min(ushort.MaxValue, lo + 1);

        using var img = new Image<L8>(width, height);
        double range = hi - lo;
        img.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < height; y++)
            {
                var row = rows.GetRowSpan(y);
                int srcOffset = y * width;
                for (int x = 0; x < width; x++)
                {
                    int v = highEnergy[srcOffset + x];
                    if (v <= lo) { row[x] = new L8(0); continue; }
                    if (v >= hi) { row[x] = new L8(255); continue; }
                    byte g = (byte)Math.Clamp((int)((v - lo) * 255.0 / range), 0, 255);
                    row[x] = new L8(g);
                }
            }
        });

        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder
        {
            ColorType = PngColorType.Grayscale,
            BitDepth = PngBitDepth.Bit8,
            CompressionLevel = PngCompressionLevel.DefaultCompression
        });
        return ms.ToArray();
    }

    /// <summary>
    /// Sample-based percentile estimate. We don't need the exact percentile —
    /// a few thousand stratified samples give a clip point that's stable
    /// scan-to-scan and ~100x cheaper than sorting 3M pixels.
    /// </summary>
    private static (ushort Low, ushort High) PercentileClipPoints(
        ushort[] pixels, double pLow, double pHigh)
    {
        const int targetSamples = 8192;
        int stride = Math.Max(1, pixels.Length / targetSamples);
        var samples = new List<ushort>(targetSamples + 16);
        for (int i = 0; i < pixels.Length; i += stride)
            samples.Add(pixels[i]);
        samples.Sort();

        int loIdx = (int)Math.Clamp(samples.Count * pLow / 100.0, 0, samples.Count - 1);
        int hiIdx = (int)Math.Clamp(samples.Count * pHigh / 100.0, 0, samples.Count - 1);
        return (samples[loIdx], samples[hiIdx]);
    }
}
