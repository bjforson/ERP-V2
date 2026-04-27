using Microsoft.Extensions.Options;

namespace NickERP.Inspection.Imaging;

/// <summary>
/// Disk-backed <see cref="IImageStore"/>. Atomic writes (temp file +
/// rename) so a reader never sees a half-written render. No locking on
/// reads — same content hash means same bytes; the OS file cache handles
/// hot paths.
/// </summary>
public sealed class DiskImageStore : IImageStore
{
    private readonly ImagingOptions _opts;

    public DiskImageStore(IOptions<ImagingOptions> opts)
    {
        _opts = opts.Value;
        if (string.IsNullOrWhiteSpace(_opts.StorageRoot))
            throw new InvalidOperationException(
                $"NickErp:Inspection:Imaging:StorageRoot is required. " +
                $"Set it in appsettings or via env var ({ImagingOptions.SectionName.Replace(":", "__")}__StorageRoot).");
        Directory.CreateDirectory(SourceRoot());
        Directory.CreateDirectory(RenderRoot());
    }

    public async Task<string> SaveSourceAsync(string contentHash, string fileExtension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var path = SourcePath(contentHash, fileExtension);
        if (File.Exists(path)) return path; // content-addressed → idempotent
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicAsync(path, bytes, ct);
        return path;
    }

    public async Task<byte[]> ReadSourceAsync(string contentHash, string fileExtension, CancellationToken ct = default)
    {
        var path = SourcePath(contentHash, fileExtension);
        if (!File.Exists(path)) throw new FileNotFoundException($"Source blob not found: {path}", path);
        return await File.ReadAllBytesAsync(path, ct);
    }

    public async Task<string> SaveRenderAsync(Guid scanArtifactId, string kind, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var path = RenderPath(scanArtifactId, kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicAsync(path, bytes, ct);
        return path;
    }

    public Stream? OpenRenderRead(Guid scanArtifactId, string kind)
    {
        var path = RenderPath(scanArtifactId, kind);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true)
            : null;
    }

    private string SourceRoot() => Path.Combine(_opts.StorageRoot, "source");
    private string RenderRoot() => Path.Combine(_opts.StorageRoot, "render");

    private string SourcePath(string contentHash, string fileExtension)
    {
        var ext = SanitizeExt(fileExtension);
        var prefix = contentHash.Length >= 2 ? contentHash[..2] : "00";
        return Path.Combine(SourceRoot(), prefix, $"{contentHash}{ext}");
    }

    private string RenderPath(Guid scanArtifactId, string kind)
        => Path.Combine(RenderRoot(), scanArtifactId.ToString("N"), $"{kind}.png");

    private static string SanitizeExt(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return "";
        var clean = ext.StartsWith('.') ? ext : "." + ext;
        foreach (var bad in Path.GetInvalidFileNameChars())
            clean = clean.Replace(bad, '_');
        return clean;
    }

    private static async Task WriteAtomicAsync(string finalPath, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var tmp = finalPath + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true))
        {
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
        }
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(tmp, finalPath);
    }
}
