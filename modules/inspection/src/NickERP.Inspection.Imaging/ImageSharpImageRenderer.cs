using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace NickERP.Inspection.Imaging;

/// <summary>
/// ImageSharp implementation of <see cref="IImageRenderer"/>. Picked over
/// SkiaSharp (native libs, deploy friction) and Magick.NET (slow, license)
/// per ARCHITECTURE §7.7 — it's pure managed code, has first-class TIFF
/// support, and matches v1's deploy story.
/// </summary>
public sealed class ImageSharpImageRenderer : IImageRenderer
{
    /// <summary>Longest-edge target for thumbnails. Matches the architecture spec.</summary>
    public const int ThumbnailMaxEdge = 256;

    /// <summary>Longest-edge target for previews. Matches the architecture spec.</summary>
    public const int PreviewMaxEdge = 1024;

    public Task<RenderedImage> RenderThumbnailAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default)
        => Task.FromResult(RenderResized(sourceBytes, ThumbnailMaxEdge, ct));

    public Task<RenderedImage> RenderPreviewAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default)
        => Task.FromResult(RenderResized(sourceBytes, PreviewMaxEdge, ct));

    private static RenderedImage RenderResized(ReadOnlyMemory<byte> sourceBytes, int maxEdge, CancellationToken ct)
    {
        if (sourceBytes.IsEmpty)
            throw new ArgumentException("Source image bytes are empty.", nameof(sourceBytes));

        ct.ThrowIfCancellationRequested();

        // Load the source. ImageSharp picks the right decoder by sniffing
        // the magic bytes — the adapter's MIME type is informational only.
        using var image = Image.Load(sourceBytes.Span);

        // Resize preserving aspect: longest edge clamped to maxEdge, no
        // upscaling. Mode.Max gives "fit inside the box".
        if (image.Width > maxEdge || image.Height > maxEdge)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(maxEdge, maxEdge),
                Mode = ResizeMode.Max,
                Sampler = KnownResamplers.Bicubic
            }));
        }

        ct.ThrowIfCancellationRequested();

        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.DefaultCompression
        });

        return new RenderedImage(
            Bytes: ms.ToArray(),
            WidthPx: image.Width,
            HeightPx: image.Height,
            MimeType: "image/png");
    }
}
