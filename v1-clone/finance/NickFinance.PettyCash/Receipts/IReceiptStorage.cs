namespace NickFinance.PettyCash.Receipts;

/// <summary>
/// Pluggable receipt-blob storage. v1 uses <see cref="LocalDiskReceiptStorage"/>
/// per the spec (Data\PettyCash\Receipts\{yyyy}\{mm}\…); migration to
/// object storage in Phase 5 swaps the implementation only.
/// </summary>
public interface IReceiptStorage
{
    /// <summary>Persist the bytes and return the path that should be stored on the receipt row.</summary>
    Task<string> SaveAsync(Guid voucherId, short ordinal, string fileName, byte[] content, CancellationToken ct = default);

    /// <summary>Read bytes back. Throws if the path doesn't exist.</summary>
    Task<byte[]> ReadAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// Default — writes under <see cref="RootDirectory"/>; partitioned by
/// upload year + month so one site doesn't pile up millions of files in
/// a single folder. File name is <c>{voucher-id}-{ordinal}.{ext}</c>.
/// </summary>
public sealed class LocalDiskReceiptStorage : IReceiptStorage
{
    public string RootDirectory { get; }
    private readonly TimeProvider _clock;

    public LocalDiskReceiptStorage(string rootDirectory, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("rootDirectory is required.", nameof(rootDirectory));
        RootDirectory = rootDirectory;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<string> SaveAsync(Guid voucherId, short ordinal, string fileName, byte[] content, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var dir = Path.Combine(RootDirectory, now.Year.ToString("D4"), now.Month.ToString("D2"));
        Directory.CreateDirectory(dir);

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        var path = Path.Combine(dir, $"{voucherId:N}-{ordinal:D2}{ext}");
        await File.WriteAllBytesAsync(path, content, ct);
        return path;
    }

    public async Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return await File.ReadAllBytesAsync(path, ct);
    }
}

/// <summary>In-memory storage for tests. Returns a synthetic <c>memory://</c> path.</summary>
public sealed class InMemoryReceiptStorage : IReceiptStorage
{
    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public Task<string> SaveAsync(Guid voucherId, short ordinal, string fileName, byte[] content, CancellationToken ct = default)
    {
        var path = $"memory://{voucherId:N}/{ordinal:D2}/{fileName}";
        _store[path] = content;
        return Task.FromResult(path);
    }

    public Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!_store.TryGetValue(path, out var bytes))
        {
            throw new FileNotFoundException($"No in-memory receipt at '{path}'.");
        }
        return Task.FromResult(bytes);
    }
}
