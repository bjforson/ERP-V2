using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// Plate-ROI preprocessor. Implements §6.1.2: long-edge resize to 384,
/// zero-pad to a 384 × 384 square, ImageNet-normalise to a CHW float32
/// tensor laid out as <c>[3, H, W]</c>. The recogniser is responsible for
/// wiring the produced span into the runner's input port.
/// </summary>
internal static class PlateRoiPreprocessor
{
    /// <summary>ImageNet mean per RGB channel — matches torchvision <c>Normalize</c> defaults.</summary>
    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };

    /// <summary>ImageNet stddev per RGB channel — matches torchvision <c>Normalize</c> defaults.</summary>
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };

    /// <summary>
    /// Preprocess the raw ROI bytes into a CHW float32 buffer of length
    /// <c>3 * targetH * targetW</c>. Bytes are decoded by ImageSharp; any
    /// supported format (PNG/JPEG/TIFF/BMP) works. Single-channel inputs
    /// are broadcast across all 3 channels (the §6.1.2 phase-2 ablation
    /// flips this to a true 1-channel tensor; out of scope today).
    /// </summary>
    public static float[] Preprocess(ReadOnlySpan<byte> bytes, int targetH, int targetW)
    {
        if (targetH <= 0 || targetW <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetH), "Target H/W must be positive.");
        }
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("Plate ROI bytes are empty.", nameof(bytes));
        }

        // ImageSharp's Load is allocation-heavy but pinned to a single bring-up
        // hot path here; the recogniser caches the loaded model so the cost is
        // amortised over many recognitions.
        using var image = Image.Load<Rgb24>(bytes);

        // Long-edge resize to 384 with bilinear interpolation; pad with zeros
        // to a square. ImageSharp's BoxPad fills with the configured background
        // color (transparent → black for Rgb24).
        var longEdge = Math.Max(image.Width, image.Height);
        if (longEdge != targetH || image.Width != image.Height)
        {
            image.Mutate(ctx => ctx
                .Resize(new ResizeOptions
                {
                    Size = new Size(targetW, targetH),
                    Mode = ResizeMode.Pad,
                    PadColor = Color.Black,
                    Sampler = KnownResamplers.Bicubic,
                }));
        }

        // The Resize(...Pad) above may end up with the canvas at exactly the
        // requested size when input is non-square. Confirm by asserting.
        if (image.Width != targetW || image.Height != targetH)
        {
            throw new InvalidOperationException(
                $"Preprocess invariant: post-resize size is {image.Width}x{image.Height}, expected {targetW}x{targetH}.");
        }

        // CHW layout: tensor[c, y, x] = (px[c]/255 - mean[c]) / std[c]
        var planeSize = targetH * targetW;
        var buffer = new float[3 * planeSize];

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var px = row[x];
                    var idx = y * targetW + x;
                    buffer[0 * planeSize + idx] = (px.R / 255f - ImageNetMean[0]) / ImageNetStd[0];
                    buffer[1 * planeSize + idx] = (px.G / 255f - ImageNetMean[1]) / ImageNetStd[1];
                    buffer[2 * planeSize + idx] = (px.B / 255f - ImageNetMean[2]) / ImageNetStd[2];
                }
            }
        });

        return buffer;
    }
}
