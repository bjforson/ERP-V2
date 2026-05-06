using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Tenancy.Database.Storage;

/// <summary>
/// Sprint 51 / Phase E — S3-compatible
/// <see cref="ITenantExportStorage"/>. Sends signed PUT/GET/DELETE
/// requests against any S3-compatible endpoint (AWS S3, Minio, Backblaze
/// B2, Wasabi). Uses AWS Signature Version 4 path-style requests so
/// the Minio dev workflow (which doesn't support virtual-host-style
/// addressing out of the box) works without special-casing.
/// </summary>
/// <remarks>
/// <para>
/// Locator shape: <c>s3://{bucket}/{tenantId}/{exportId:N}.zip</c>. The
/// runner stores the locator in <c>TenantExportRequest.ArtifactPath</c>;
/// the download path passes it back to <see cref="OpenReadAsync"/> to
/// stream the bundle.
/// </para>
/// <para>
/// We intentionally ship our own thin HTTP-based S3 client rather than
/// pulling in <c>AWSSDK.S3</c>: the surface we need is three operations
/// on a single object (no multipart, no listing), and the AWS SDK drags
/// in a sizeable dependency tree (<c>AWSSDK.Core</c> + several
/// transitive deps). Once bundles regularly exceed ~5 GB the multipart
/// upload story would push us toward the SDK; today's single-PutObject
/// fits well in a homegrown client.
/// </para>
/// <para>
/// Configuration via <see cref="S3StorageOptions"/>; bound from the
/// <c>Tenancy:Export:Storage:S3</c> section by
/// <see cref="TenancyDatabaseServiceCollectionExtensions.AddNickErpTenantExport"/>.
/// </para>
/// </remarks>
public sealed class S3TenantExportStorage : ITenantExportStorage
{
    private readonly S3StorageOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<S3TenantExportStorage> _logger;

    public S3TenantExportStorage(
        S3StorageOptions options,
        HttpClient http,
        ILogger<S3TenantExportStorage> logger)
    {
        _options = options;
        _http = http;
        _logger = logger;
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
            throw new ArgumentException("S3 Endpoint is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.Bucket))
            throw new ArgumentException("S3 Bucket is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.AccessKey))
            throw new ArgumentException("S3 AccessKey is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ArgumentException("S3 SecretKey is required.", nameof(options));
    }

    /// <inheritdoc />
    public string BackendName => "s3";

    /// <inheritdoc />
    public async Task<string> WriteAsync(Guid exportId, long tenantId, byte[] bytes, CancellationToken ct = default)
    {
        var key = BuildKey(tenantId, exportId);
        var uri = BuildObjectUri(key);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var req = new HttpRequestMessage(HttpMethod.Put, uri) { Content = content };
        SignRequest(req, "PUT", key, bytes);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"S3 PUT to {uri} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }
        var locator = $"s3://{_options.Bucket}/{key}";
        _logger.LogInformation(
            "S3TenantExportStorage wrote {Bytes} bytes to {Locator}.", bytes.Length, locator);
        return locator;
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(string locator, CancellationToken ct = default)
    {
        var key = ExtractKeyFromLocator(locator);
        if (key is null) return null;
        var uri = BuildObjectUri(key);
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        SignRequest(req, "GET", key, payload: Array.Empty<byte>());
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            resp.Dispose();
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new InvalidOperationException(
                $"S3 GET from {uri} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }
        // Wrap the response so disposing the stream also disposes the
        // HttpResponseMessage. Caller disposes.
        return new HttpResponseStreamWrapper(await resp.Content.ReadAsStreamAsync(ct), resp);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string locator, CancellationToken ct = default)
    {
        var key = ExtractKeyFromLocator(locator);
        if (key is null) return false;
        var uri = BuildObjectUri(key);
        using var req = new HttpRequestMessage(HttpMethod.Delete, uri);
        SignRequest(req, "DELETE", key, payload: Array.Empty<byte>());
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"S3 DELETE on {uri} failed: {(int)resp.StatusCode} {resp.ReasonPhrase}. Body: {body}");
        }
        return true;
    }

    private string BuildKey(long tenantId, Guid exportId)
        => $"{tenantId}/{exportId:N}.zip";

    private Uri BuildObjectUri(string key)
    {
        // Path-style addressing: https://endpoint/bucket/key. Works for
        // AWS S3 and every S3-compatible service we care about (Minio
        // is the dev target).
        var endpointBase = _options.Endpoint!.TrimEnd('/');
        return new Uri($"{endpointBase}/{_options.Bucket}/{key}");
    }

    private string? ExtractKeyFromLocator(string locator)
    {
        if (string.IsNullOrWhiteSpace(locator)) return null;
        const string prefix = "s3://";
        if (!locator.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = locator.Substring(prefix.Length);
        var slashIndex = rest.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == rest.Length - 1) return null;
        // We don't enforce bucket-name match here — operators can move
        // bundles between buckets without rewriting old rows.
        return rest.Substring(slashIndex + 1);
    }

    /// <summary>
    /// AWS Signature V4 signing. Implementation follows the canonical
    /// algorithm from the AWS docs; it's small enough that pulling in
    /// the AWS SDK just for this would be wasteful.
    /// </summary>
    private void SignRequest(HttpRequestMessage req, string method, string key, byte[] payload)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var payloadHash = Sha256Hex(payload);

        var host = req.RequestUri!.Host + (req.RequestUri.IsDefaultPort ? string.Empty : $":{req.RequestUri.Port}");
        req.Headers.TryAddWithoutValidation("Host", host);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var canonicalUri = req.RequestUri.AbsolutePath;
        var canonicalQuery = string.Empty; // we don't use query params
        // SignedHeaders are case-insensitive but lowercased in the
        // canonical request. host + x-amz-content-sha256 + x-amz-date.
        var signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalHeaders =
            $"host:{host}\n" +
            $"x-amz-content-sha256:{payloadHash}\n" +
            $"x-amz-date:{amzDate}\n";
        var canonicalRequest =
            $"{method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var credentialScope = $"{dateStamp}/{_options.Region}/s3/aws4_request";
        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))}";

        var kSecret = Encoding.UTF8.GetBytes("AWS4" + _options.SecretKey);
        var kDate = HmacSha256(kSecret, dateStamp);
        var kRegion = HmacSha256(kDate, _options.Region);
        var kService = HmacSha256(kRegion, "s3");
        var kSigning = HmacSha256(kService, "aws4_request");
        var signature = ToHex(HmacSha256(kSigning, stringToSign));

        var authHeader =
            $"AWS4-HMAC-SHA256 Credential={_options.AccessKey}/{credentialScope}, "
            + $"SignedHeaders={signedHeaders}, Signature={signature}";
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(bytes));
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>
    /// Wraps the HTTP response stream so disposing closes the
    /// underlying response too. Without this the Open file
    /// descriptor leaks until GC.
    /// </summary>
    private sealed class HttpResponseStreamWrapper : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _resp;
        public HttpResponseStreamWrapper(Stream inner, HttpResponseMessage resp)
        {
            _inner = inner;
            _resp = resp;
        }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => _inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            try { _inner.Dispose(); } catch { /* best-effort */ }
            try { _resp.Dispose(); } catch { /* best-effort */ }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Sprint 51 / Phase E — configuration for
/// <see cref="S3TenantExportStorage"/>. Bound from
/// <c>Tenancy:Export:Storage:S3</c>.
/// </summary>
public sealed class S3StorageOptions
{
    /// <summary>HTTP endpoint base, e.g. <c>https://s3.amazonaws.com</c>
    /// or <c>http://minio:9000</c>. No trailing slash required.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Bucket name (lowercase ASCII per S3 conventions).</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Access key id.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret access key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>AWS region. Default <c>us-east-1</c> — Minio's default
    /// signing region too.</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>Whether to require https. Default true; set false for
    /// dev Minio over plain http.</summary>
    public bool UseSsl { get; set; } = true;
}
