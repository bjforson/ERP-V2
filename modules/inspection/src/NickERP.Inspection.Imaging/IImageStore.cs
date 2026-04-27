namespace NickERP.Inspection.Imaging;

/// <summary>
/// Persistence for the image pipeline. Two namespaces live under it:
///
///   - <b>Source</b> — verbatim bytes the scanner adapter produced
///     (<c>parsed.Bytes</c> from <see cref="Scanners.Abstractions.IScannerAdapter.ParseAsync"/>).
///     Content-addressed by SHA-256: <c>source/{hash[0..2]}/{hash}.{ext}</c>.
///     Many ScanArtifacts can share one source blob (idempotent re-renders,
///     re-ingest of the same file, etc.).
///
///   - <b>Render</b> — derived thumbnails + previews. Keyed by
///     ScanArtifactId because each render is per-artifact (different
///     percentile clips, different orientation, etc. in future revisions).
///     Path: <c>render/{scanArtifactId}/{kind}.png</c>.
///
/// Skeleton stays in disk for now; Redis routing for thumbnails moves in
/// when we have the latency baseline (ARCHITECTURE §7.7 acceptance bar:
/// thumbs ≤ 50 ms p95).
/// </summary>
public interface IImageStore
{
    /// <summary>Persist source bytes; returns the storage URI used.</summary>
    Task<string> SaveSourceAsync(string contentHash, string fileExtension, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    /// <summary>Read source bytes back. Throws <see cref="FileNotFoundException"/> if missing.</summary>
    Task<byte[]> ReadSourceAsync(string contentHash, string fileExtension, CancellationToken ct = default);

    /// <summary>Persist a rendered derivative; returns the storage URI used.</summary>
    Task<string> SaveRenderAsync(Guid scanArtifactId, string kind, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    /// <summary>
    /// Open a stream over a previously-saved render. Caller disposes.
    /// Returns <see langword="null"/> when the render isn't present yet
    /// (the worker hasn't gotten to it, or it was evicted).
    /// </summary>
    Stream? OpenRenderRead(Guid scanArtifactId, string kind);
}
