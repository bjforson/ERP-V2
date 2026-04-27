using System.Collections.Concurrent;
using System.Text.Json;

namespace NickERP.Inspection.ExternalSystems.IcumsGh;

/// <summary>
/// In-memory index of ICUMS batch JSON files in <c>BatchDropPath</c>, keyed
/// by container number. Per-file mtime is tracked so we only re-parse files
/// that have actually changed since the last refresh.
///
/// Why an index — ICUMS batches can be hundreds of MB each. Re-parsing every
/// file on every <c>FetchDocumentsAsync</c> call would be untenable;
/// pre-indexing by container number turns each fetch into a dictionary
/// lookup plus one targeted JSON re-read.
///
/// Memory model — the index stores the **serialized JSON for matched
/// documents** (not parsed object graphs). One BoeScanDocument per container
/// is typically 1-4 KB; for a batch with 500 containers that's ~2 MB
/// resident. Cheaper than re-parsing on every fetch and lets
/// <see cref="IExternalSystemAdapter.FetchDocumentsAsync"/> return raw JSON
/// payloads as required by the v2 contract without a re-serialize round-trip.
///
/// Thread safety — the index uses <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// for the root container map; refreshes lock on a per-instance gate so two
/// concurrent fetches don't both walk the directory.
/// </summary>
internal sealed class IcumBatchIndex
{
    private readonly object _refreshLock = new();
    private readonly ConcurrentDictionary<string, FileEntry> _filesByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<DocumentEntry>> _byContainer = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastRefreshUtc = DateTimeOffset.MinValue;

    public string DropPath { get; }
    public TimeSpan CacheTtl { get; }

    public IcumBatchIndex(string dropPath, TimeSpan cacheTtl)
    {
        DropPath = dropPath ?? throw new ArgumentNullException(nameof(dropPath));
        CacheTtl = cacheTtl;
    }

    /// <summary>
    /// Re-walk the drop folder if the cache is stale or any file's mtime has
    /// drifted from what we last saw. Cheap when nothing has changed.
    /// </summary>
    public void RefreshIfStale()
    {
        if (DateTimeOffset.UtcNow - _lastRefreshUtc < CacheTtl)
            return;

        lock (_refreshLock)
        {
            if (DateTimeOffset.UtcNow - _lastRefreshUtc < CacheTtl) return;

            if (!Directory.Exists(DropPath))
            {
                // Folder doesn't exist — keep whatever index we have, but
                // mark refreshed so we don't hammer the FS on every call.
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                return;
            }

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(DropPath, "*.json", SearchOption.AllDirectories))
            {
                seenPaths.Add(path);
                var mtime = File.GetLastWriteTimeUtc(path);

                if (_filesByPath.TryGetValue(path, out var existing) && existing.MtimeUtc == mtime)
                    continue;

                ReindexFile(path, mtime);
            }

            // Drop entries for files that no longer exist.
            foreach (var stalePath in _filesByPath.Keys.Where(p => !seenPaths.Contains(p)).ToArray())
                EvictFile(stalePath);

            _lastRefreshUtc = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Look up every document we know about for a given container number.</summary>
    public IReadOnlyList<DocumentEntry> GetByContainer(string containerNumber)
    {
        if (string.IsNullOrWhiteSpace(containerNumber)) return Array.Empty<DocumentEntry>();
        return _byContainer.TryGetValue(containerNumber.Trim(), out var list)
            ? list
            : Array.Empty<DocumentEntry>();
    }

    /// <summary>Diagnostics: total indexed file count, total document count, last refresh time.</summary>
    public (int Files, int Documents, DateTimeOffset LastRefresh) Stats()
        => (_filesByPath.Count, _filesByPath.Values.Sum(f => f.DocumentCount), _lastRefreshUtc);

    /// <summary>
    /// Enumerate every indexed document across every container. Used by the
    /// adapter's slow-path lookup when the host has only a declaration
    /// number / BL, no container.
    /// </summary>
    public IEnumerable<DocumentEntry> EnumerateAll()
    {
        foreach (var bucket in _byContainer.Values)
            foreach (var entry in bucket) yield return entry;
    }

    private void ReindexFile(string path, DateTime mtimeUtc)
    {
        EvictFile(path);

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: false);
            using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions
            {
                MaxDepth = 128,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (!doc.RootElement.TryGetProperty("BOEScanDocument", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                _filesByPath[path] = new FileEntry(mtimeUtc, 0);
                return;
            }

            int count = 0;
            foreach (var docEl in arr.EnumerateArray())
            {
                if (!docEl.TryGetProperty("ContainerDetails", out var cd)) continue;
                if (!cd.TryGetProperty("ContainerNumber", out var cn)) continue;
                var container = cn.GetString();
                if (string.IsNullOrWhiteSpace(container)) continue;

                // Pull declaration number + house BL out of the same blob so
                // ReferenceNumber and DocumentType are cheap to populate later.
                string declarationNumber = TryReadString(docEl, "Header", "DeclarationNumber") ?? "";
                string houseBl = TryReadString(docEl, "ManifestDetails", "HouseBL") ?? "";
                string regimeCode = TryReadString(docEl, "Header", "RegimeCode") ?? "";

                // Snapshot the raw JSON for this document only — we don't keep
                // the JsonDocument alive past this call.
                var rawJson = docEl.GetRawText();

                var entry = new DocumentEntry(
                    SourceFilePath: path,
                    ContainerNumber: container.Trim(),
                    DeclarationNumber: declarationNumber,
                    HouseBlNumber: houseBl,
                    RegimeCode: regimeCode,
                    RawJson: rawJson);

                _byContainer.AddOrUpdate(
                    entry.ContainerNumber,
                    _ => new List<DocumentEntry> { entry },
                    (_, list) => { list.Add(entry); return list; });

                count++;
            }

            _filesByPath[path] = new FileEntry(mtimeUtc, count);
        }
        catch (Exception)
        {
            // A malformed file shouldn't poison the entire index. Record an
            // empty entry so we don't keep retrying it on every refresh.
            _filesByPath[path] = new FileEntry(mtimeUtc, 0);
        }
    }

    private void EvictFile(string path)
    {
        if (!_filesByPath.TryRemove(path, out _)) return;

        // Drop every per-container entry that came from this file.
        foreach (var key in _byContainer.Keys.ToArray())
        {
            if (!_byContainer.TryGetValue(key, out var list)) continue;
            var trimmed = list.Where(e => !string.Equals(e.SourceFilePath, path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (trimmed.Count == 0) _byContainer.TryRemove(key, out _);
            else _byContainer[key] = trimmed;
        }
    }

    private static string? TryReadString(JsonElement parent, string p1, string p2)
    {
        if (!parent.TryGetProperty(p1, out var lvl1)) return null;
        if (!lvl1.TryGetProperty(p2, out var lvl2)) return null;
        return lvl2.ValueKind == JsonValueKind.String ? lvl2.GetString() : null;
    }

    public sealed record FileEntry(DateTime MtimeUtc, int DocumentCount);

    public sealed record DocumentEntry(
        string SourceFilePath,
        string ContainerNumber,
        string DeclarationNumber,
        string HouseBlNumber,
        string RegimeCode,
        string RawJson);
}
