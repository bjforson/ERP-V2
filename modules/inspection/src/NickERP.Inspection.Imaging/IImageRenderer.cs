namespace NickERP.Inspection.Imaging;

/// <summary>
/// Renders source artifact bytes (typically a PNG produced by an
/// <c>IScannerAdapter.ParseAsync</c>) into derived sizes for the analyst
/// viewer + cases list.
///
/// The pipeline keeps the source — what the adapter produced — and
/// generates two derivatives: a 256 px thumbnail and a 1024 px preview.
/// Both preserve aspect ratio (the named size is the longest edge); both
/// are PNG today. Future work will swap WebP in here once browser support
/// across Cloudflare-Access-protected origins is verified.
///
/// <see cref="IImageRenderer"/> is single-purpose and stateless on
/// purpose: every input it reads is on the call site, every output is
/// what it returns. The worker that schedules renders, the store that
/// persists them, and the endpoint that serves them are orthogonal.
/// </summary>
public interface IImageRenderer
{
    /// <summary>Render a thumbnail (256 px on the longest edge).</summary>
    Task<RenderedImage> RenderThumbnailAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default);

    /// <summary>Render a preview (1024 px on the longest edge).</summary>
    Task<RenderedImage> RenderPreviewAsync(ReadOnlyMemory<byte> sourceBytes, CancellationToken ct = default);
}

/// <summary>One derived image produced by <see cref="IImageRenderer"/>.</summary>
/// <param name="Bytes">PNG-encoded payload.</param>
/// <param name="WidthPx">Final width.</param>
/// <param name="HeightPx">Final height.</param>
/// <param name="MimeType">Always <c>image/png</c> in this revision.</param>
public sealed record RenderedImage(
    byte[] Bytes,
    int WidthPx,
    int HeightPx,
    string MimeType);

/// <summary>Render kind names used as URL segments and DB enum-string values.</summary>
public static class RenderKinds
{
    public const string Thumbnail = "thumbnail";
    public const string Preview = "preview";

    public static bool IsKnown(string? kind) =>
        string.Equals(kind, Thumbnail, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Preview, StringComparison.OrdinalIgnoreCase);
}
