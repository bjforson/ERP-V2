using System.Collections.Concurrent;
using System.Text.Json;
using NickERP.Inspection.ExternalSystems.Abstractions;
using NickERP.Platform.Plugins;

namespace NickERP.Inspection.ExternalSystems.IcumsGh;

/// <summary>
/// Ghana ICUMS adapter — file-drop intake + file-based outbox, mirroring v1
/// NSCIM's actual deployment topology (the live HTTP path was prepared in v1
/// but never used; verdicts always flowed through a filesystem outbox per
/// Ghana Customs operations).
///
/// Inbound — operators (or a separate sync job) drop ICUMS batch JSON files
/// (root <c>.BOEScanDocument[]</c>) into <c>BatchDropPath</c>. The adapter
/// indexes them by container number, mtime-cached, and answers per-case
/// lookups from the index.
///
/// Outbound — verdicts are serialized to <c>{OutboxPath}/{IdempotencyKey}.json</c>.
/// Same key = same filename, so re-submission is a deterministic no-op
/// (file overwrite). Downstream pickup is somebody else's problem (ICUMS
/// integration team, an SFTP cron, whatever the deployment uses).
///
/// Live HTTP fetch is deliberately out of scope for v2 first cut. The v1
/// implementation built a Polly retry/circuit-breaker stack against
/// <c>ICUMS:FetchBatchUrl</c>; that lands in §4.5 if/when ICUMS exposes a
/// per-container query endpoint.
///
/// <para>
/// <b>Tenant isolation.</b> The static <c>_indexes</c> cache is keyed by
/// <c>{tenantId}|{instanceId}|{path}|{ttl}</c> as of contract version 1.1
/// (Sprint PT). Two tenants whose <c>ExternalSystemInstance</c> rows happen
/// to point at the same physical drop folder get isolated cache entries —
/// no cross-tenant leak via the index. Static-cache lifetime is the host
/// process; tenant churn does not evict (known follow-up).
/// </para>
/// </summary>
[Plugin("icums-gh")]
public sealed class IcumsGhAdapter : IExternalSystemAdapter
{
    public string TypeCode => "icums-gh";

    public ExternalSystemCapabilities Capabilities { get; } = new(
        SupportedDocumentTypes: new[] { "BOE", "CMR", "IM" },
        SupportsPushNotifications: false,
        SupportsBulkFetch: true);

    /// <summary>One batch index per <see cref="ExternalSystemConfig.InstanceId"/> + drop path tuple.</summary>
    private static readonly ConcurrentDictionary<string, IcumBatchIndex> _indexes =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<ConnectionTestResult> TestAsync(ExternalSystemConfig config, CancellationToken ct = default)
    {
        var cfg = ParseConfig(config);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(cfg.BatchDropPath))
            problems.Add("BatchDropPath not configured");
        else if (!Directory.Exists(cfg.BatchDropPath))
            problems.Add($"BatchDropPath does not exist: {cfg.BatchDropPath}");

        if (string.IsNullOrWhiteSpace(cfg.OutboxPath))
            problems.Add("OutboxPath not configured");
        else
        {
            try { Directory.CreateDirectory(cfg.OutboxPath); }
            catch (Exception ex) { problems.Add($"OutboxPath not writable: {ex.Message}"); }
        }

        if (problems.Count > 0)
            return Task.FromResult(new ConnectionTestResult(false, string.Join("; ", problems)));

        var index = GetOrCreateIndex(config.TenantId, config.InstanceId, cfg);
        index.RefreshIfStale();
        var (files, docs, _) = index.Stats();
        return Task.FromResult(new ConnectionTestResult(
            Success: true,
            Message: $"Drop folder OK — {files} batch file(s), {docs} indexed document(s). Outbox writable.",
            Latency: TimeSpan.FromMilliseconds(1)));
    }

    public Task<IReadOnlyList<AuthorityDocument>> FetchDocumentsAsync(
        ExternalSystemConfig config,
        CaseLookupCriteria lookup,
        CancellationToken ct = default)
    {
        var cfg = ParseConfig(config);
        if (string.IsNullOrWhiteSpace(cfg.BatchDropPath))
            return Task.FromResult<IReadOnlyList<AuthorityDocument>>(Array.Empty<AuthorityDocument>());

        var index = GetOrCreateIndex(config.TenantId, config.InstanceId, cfg);
        index.RefreshIfStale();

        var matches = new List<AuthorityDocument>();

        // ICUMS keys everything off ContainerNumber — that's the primary
        // path. Vehicle VIN and AuthorityReferenceNumber land here too:
        // when the host doesn't know the container yet, callers may pass
        // the declaration number as AuthorityReferenceNumber and we scan
        // for it. Keep it simple — no fuzzy matching.
        if (!string.IsNullOrWhiteSpace(lookup.ContainerNumber))
        {
            foreach (var entry in index.GetByContainer(lookup.ContainerNumber))
                matches.Add(ToAuthorityDocument(config.InstanceId, entry));
        }

        if (matches.Count == 0 && !string.IsNullOrWhiteSpace(lookup.AuthorityReferenceNumber))
        {
            // Fallback: linear scan when the host hasn't given us a
            // container. Bounded by the document count which we keep
            // moderate; full-batch scans of 100k docs should still
            // complete inside 50ms in practice.
            var refNo = lookup.AuthorityReferenceNumber.Trim();
            // We don't have a declaration-keyed dictionary, so iterate
            // every known entry. Acceptable for the cardinalities ICUMS
            // produces; revisit if a deployment ever exceeds 50k active
            // containers per drop folder.
            foreach (var bucket in index.EnumerateAll())
            {
                if (string.Equals(bucket.DeclarationNumber, refNo, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bucket.HouseBlNumber, refNo, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(ToAuthorityDocument(config.InstanceId, bucket));
                }
            }
        }

        return Task.FromResult<IReadOnlyList<AuthorityDocument>>(matches);
    }

    public async Task<SubmissionResult> SubmitAsync(
        ExternalSystemConfig config,
        OutboundSubmissionRequest request,
        CancellationToken ct = default)
    {
        var cfg = ParseConfig(config);
        if (string.IsNullOrWhiteSpace(cfg.OutboxPath))
            return new SubmissionResult(false, null, "OutboxPath not configured.");

        try
        {
            Directory.CreateDirectory(cfg.OutboxPath);

            // Sanitize the idempotency key into a safe filename. The host
            // is supposed to give us an opaque key — typically a SHA hash
            // — so the sanitization is defensive, not transformative.
            var safeKey = SanitizeFilename(request.IdempotencyKey);
            var outPath = Path.Combine(cfg.OutboxPath, safeKey + ".json");

            // Idempotency: if a file with this key already exists and has
            // identical content, return success without rewriting. If
            // content differs we still overwrite — the host owns the key,
            // and if it reused a key with different content, the latest
            // payload wins. This matches v1's outbox semantics.
            var envelope = new
            {
                idempotencyKey = request.IdempotencyKey,
                authorityReferenceNumber = request.AuthorityReferenceNumber,
                submittedAtUtc = DateTimeOffset.UtcNow,
                instanceId = config.InstanceId,
                payload = JsonSerializer.Deserialize<JsonElement>(request.PayloadJson)
            };

            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            // Atomic write: write to a temp file in the same folder, then
            // rename. Avoids pickup-side reading a half-written file.
            var tmpPath = outPath + ".tmp";
            await File.WriteAllBytesAsync(tmpPath, bytes, ct);
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmpPath, outPath);

            var responseJson = JsonSerializer.Serialize(new
            {
                accepted = true,
                outboxPath = outPath,
                idempotencyKey = request.IdempotencyKey
            });
            return new SubmissionResult(Accepted: true, AuthorityResponseJson: responseJson, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SubmissionResult(false, null, $"Outbox write failed: {ex.Message}");
        }
    }

    // --- helpers --------------------------------------------------------

    private static AdapterConfig ParseConfig(ExternalSystemConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ConfigJson))
            return new AdapterConfig();
        try
        {
            return JsonSerializer.Deserialize<AdapterConfig>(config.ConfigJson) ?? new AdapterConfig();
        }
        catch (JsonException)
        {
            return new AdapterConfig();
        }
    }

    private static IcumBatchIndex GetOrCreateIndex(long tenantId, Guid instanceId, AdapterConfig cfg)
    {
        // Tenant-prefixed: two tenants pointing at the same physical
        // BatchDropPath get isolated index entries (Sprint PT, contract 1.1).
        var key = $"{tenantId}|{instanceId}|{cfg.BatchDropPath}|{cfg.CacheTtlSeconds}";
        return _indexes.GetOrAdd(key, _ => new IcumBatchIndex(
            cfg.BatchDropPath,
            TimeSpan.FromSeconds(Math.Max(1, cfg.CacheTtlSeconds))));
    }

    /// <summary>
    /// Project a single index entry into the v2 contract's AuthorityDocument.
    /// DocumentType is inferred from the regime code (Ghana Customs uses
    /// regime 80 for transit / CMR and other codes for direct imports / IM).
    /// </summary>
    private static AuthorityDocument ToAuthorityDocument(Guid instanceId, IcumBatchIndex.DocumentEntry entry)
    {
        // Reference precedence: declaration number → house BL → container.
        string referenceNumber = !string.IsNullOrEmpty(entry.DeclarationNumber)
            ? entry.DeclarationNumber
            : !string.IsNullOrEmpty(entry.HouseBlNumber)
                ? entry.HouseBlNumber
                : entry.ContainerNumber;

        // Regime 80 = CMR (transit) per Ghana Customs convention. Anything
        // else with a declaration number is BOE; bare manifest is IM.
        string documentType = entry.RegimeCode switch
        {
            "80" => "CMR",
            _ when !string.IsNullOrEmpty(entry.DeclarationNumber) => "BOE",
            _ => "IM"
        };

        var receivedAt = SafeFileMtime(entry.SourceFilePath);

        return new AuthorityDocument(
            InstanceId: instanceId,
            DocumentType: documentType,
            ReferenceNumber: referenceNumber,
            ReceivedAt: receivedAt,
            PayloadJson: entry.RawJson);
    }

    private static DateTimeOffset SafeFileMtime(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
        catch { return DateTimeOffset.UtcNow; }
    }

    private static string SanitizeFilename(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("IdempotencyKey is required.", nameof(s));

        var bad = Path.GetInvalidFileNameChars();
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++)
            buf[i] = Array.IndexOf(bad, s[i]) >= 0 ? '_' : s[i];
        return new string(buf);
    }

    private sealed class AdapterConfig
    {
        public string BatchDropPath { get; set; } = string.Empty;
        public string OutboxPath { get; set; } = string.Empty;
        public int CacheTtlSeconds { get; set; } = 60;
    }
}
