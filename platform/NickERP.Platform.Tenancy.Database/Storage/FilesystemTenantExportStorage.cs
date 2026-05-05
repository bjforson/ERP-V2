using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Tenancy.Database.Storage;

/// <summary>
/// Sprint 51 / Phase E — filesystem-backed
/// <see cref="ITenantExportStorage"/>. Mirrors the pre-Phase-E
/// behaviour: bundles land under
/// <c>{root}/{tenantId}/{exportId:N}.zip</c>; the absolute path is the
/// locator. Default storage backend when no
/// <c>Tenancy:Export:Storage:Type</c> is configured.
/// </summary>
public sealed class FilesystemTenantExportStorage : ITenantExportStorage
{
    private readonly string _rootPath;
    private readonly ILogger<FilesystemTenantExportStorage> _logger;

    public FilesystemTenantExportStorage(string rootPath, ILogger<FilesystemTenantExportStorage> logger)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path is required.", nameof(rootPath));
        }
        _rootPath = Path.IsPathRooted(rootPath)
            ? rootPath
            : Path.Combine(AppContext.BaseDirectory, rootPath);
        _logger = logger;
    }

    /// <inheritdoc />
    public string BackendName => "filesystem";

    /// <inheritdoc />
    public async Task<string> WriteAsync(Guid exportId, long tenantId, byte[] bytes, CancellationToken ct = default)
    {
        var dir = Path.Combine(_rootPath, tenantId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{exportId:N}.zip");

        // Write atomically: write to a temp sibling then rename. Safer
        // than truncating-in-place if the host crashes mid-write.
        var tempPath = path + ".tmp";
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* best-effort */ }
        }
        await using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await fs.WriteAsync(bytes, ct);
        }
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(tempPath, path);
        return path;
    }

    /// <inheritdoc />
    public Task<Stream?> OpenReadAsync(string locator, CancellationToken ct = default)
    {
        if (!File.Exists(locator))
        {
            _logger.LogDebug("FilesystemTenantExportStorage.OpenReadAsync — {Path} not found.", locator);
            return Task.FromResult<Stream?>(null);
        }
        var stream = new FileStream(locator, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string locator, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(locator) || !File.Exists(locator))
        {
            return Task.FromResult(false);
        }
        try
        {
            File.Delete(locator);
            return Task.FromResult(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "FilesystemTenantExportStorage.DeleteAsync failed for {Path}.", locator);
            return Task.FromResult(false);
        }
    }
}
