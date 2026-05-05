using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Tenancy.Database.Storage;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 51 / Phase E — exercises both
/// <see cref="ITenantExportStorage"/> implementations.
/// <see cref="FilesystemTenantExportStorage"/> is exercised against a
/// real temp directory (no provider needed); <see cref="S3TenantExportStorage"/>
/// uses a custom <c>HttpMessageHandler</c> that records requests so we
/// can verify the right HTTP verbs / paths / headers reach an
/// S3-compatible endpoint without running a real Minio.
/// </summary>
public sealed class TenantExportStorageTests : IDisposable
{
    private readonly string _tempRoot;

    public TenantExportStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "tenant-export-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FilesystemStorage_WriteOpenReadDelete_RoundTripsBytes()
    {
        var storage = new FilesystemTenantExportStorage(
            _tempRoot, NullLogger<FilesystemTenantExportStorage>.Instance);
        storage.BackendName.Should().Be("filesystem");

        var exportId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("hello-fs-storage");
        var locator = await storage.WriteAsync(exportId, tenantId: 7, bytes);

        locator.Should().Contain("7");
        locator.Should().Contain(exportId.ToString("N"));
        File.Exists(locator).Should().BeTrue();

        await using (var stream = await storage.OpenReadAsync(locator))
        {
            stream.Should().NotBeNull();
            using var sr = new StreamReader(stream!);
            var read = await sr.ReadToEndAsync();
            read.Should().Be("hello-fs-storage");
        }

        var deleted = await storage.DeleteAsync(locator);
        deleted.Should().BeTrue();
        File.Exists(locator).Should().BeFalse();

        // Idempotent: deleting twice returns false the second time.
        var deletedAgain = await storage.DeleteAsync(locator);
        deletedAgain.Should().BeFalse();

        // Reading deleted artifact returns null (not throw).
        var missing = await storage.OpenReadAsync(locator);
        missing.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FilesystemStorage_WriteOverwritesExisting()
    {
        var storage = new FilesystemTenantExportStorage(
            _tempRoot, NullLogger<FilesystemTenantExportStorage>.Instance);
        var exportId = Guid.NewGuid();

        var first = await storage.WriteAsync(exportId, 5, Encoding.UTF8.GetBytes("first"));
        var second = await storage.WriteAsync(exportId, 5, Encoding.UTF8.GetBytes("second-overwrite"));
        first.Should().Be(second, "same exportId+tenantId should land at the same path");
        var read = await File.ReadAllTextAsync(second);
        read.Should().Be("second-overwrite");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task S3Storage_WritePut_HitsRightEndpoint()
    {
        // Mock S3 endpoint via a recording HttpMessageHandler. Verifies
        // the bytes go out as a PUT to /bucket/{tenantId}/{exportId}.zip
        // with the Authorization + x-amz-* signing headers in place.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var http = new HttpClient(handler);
        var storage = new S3TenantExportStorage(
            new S3StorageOptions
            {
                Endpoint = "http://minio.test:9000",
                Bucket = "tenant-exports",
                AccessKey = "AK",
                SecretKey = "SK",
                Region = "us-east-1",
            }, http, NullLogger<S3TenantExportStorage>.Instance);
        storage.BackendName.Should().Be("s3");

        var exportId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("hello-s3");
        var locator = await storage.WriteAsync(exportId, tenantId: 9, bytes);

        locator.Should().Be($"s3://tenant-exports/9/{exportId:N}.zip");
        handler.Requests.Should().HaveCount(1);
        var req = handler.Requests[0];
        req.Method.Should().Be(HttpMethod.Put);
        req.RequestUri!.AbsolutePath.Should().Be($"/tenant-exports/9/{exportId:N}.zip");
        req.Headers.Should().Contain(h => h.Key == "Authorization");
        req.Headers.GetValues("Authorization").First().Should().StartWith("AWS4-HMAC-SHA256 ");
        req.Headers.GetValues("x-amz-date").First().Should().NotBeNullOrEmpty();
        req.Headers.GetValues("x-amz-content-sha256").First().Should().NotBeNullOrEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task S3Storage_OpenReadGet_HitsRightEndpoint_AndStreamsBody()
    {
        var bodyBytes = Encoding.UTF8.GetBytes("body-from-s3");
        var handler = new RecordingHandler(req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new ByteArrayContent(bodyBytes);
            return resp;
        });
        var http = new HttpClient(handler);
        var storage = new S3TenantExportStorage(
            new S3StorageOptions
            {
                Endpoint = "http://minio.test:9000",
                Bucket = "tenant-exports",
                AccessKey = "AK",
                SecretKey = "SK",
                Region = "us-east-1",
            }, http, NullLogger<S3TenantExportStorage>.Instance);

        var locator = "s3://tenant-exports/9/abcdef.zip";
        await using (var stream = await storage.OpenReadAsync(locator))
        {
            stream.Should().NotBeNull();
            using var ms = new MemoryStream();
            await stream!.CopyToAsync(ms);
            ms.ToArray().Should().Equal(bodyBytes);
        }
        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/tenant-exports/9/abcdef.zip");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task S3Storage_OpenRead_404ReturnsNull()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler);
        var storage = new S3TenantExportStorage(
            new S3StorageOptions
            {
                Endpoint = "http://minio.test:9000",
                Bucket = "tenant-exports",
                AccessKey = "AK",
                SecretKey = "SK",
                Region = "us-east-1",
            }, http, NullLogger<S3TenantExportStorage>.Instance);

        var stream = await storage.OpenReadAsync("s3://tenant-exports/9/missing.zip");
        stream.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task S3Storage_DeleteDelete_HitsRightEndpoint()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var http = new HttpClient(handler);
        var storage = new S3TenantExportStorage(
            new S3StorageOptions
            {
                Endpoint = "http://minio.test:9000",
                Bucket = "tenant-exports",
                AccessKey = "AK",
                SecretKey = "SK",
                Region = "us-east-1",
            }, http, NullLogger<S3TenantExportStorage>.Instance);

        var locator = "s3://tenant-exports/9/abcdef.zip";
        var deleted = await storage.DeleteAsync(locator);
        deleted.Should().BeTrue();

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].Method.Should().Be(HttpMethod.Delete);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/tenant-exports/9/abcdef.zip");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task S3Storage_Delete_404ReturnsFalse()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var http = new HttpClient(handler);
        var storage = new S3TenantExportStorage(
            new S3StorageOptions
            {
                Endpoint = "http://minio.test:9000",
                Bucket = "tenant-exports",
                AccessKey = "AK",
                SecretKey = "SK",
                Region = "us-east-1",
            }, http, NullLogger<S3TenantExportStorage>.Instance);

        var deleted = await storage.DeleteAsync("s3://tenant-exports/9/missing.zip");
        deleted.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task S3Storage_Write_NonSuccessThrows()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("internal error")
        });
        var http = new HttpClient(handler);
        var storage = new S3TenantExportStorage(
            new S3StorageOptions
            {
                Endpoint = "http://minio.test:9000",
                Bucket = "tenant-exports",
                AccessKey = "AK",
                SecretKey = "SK",
                Region = "us-east-1",
            }, http, NullLogger<S3TenantExportStorage>.Instance);

        var act = async () => await storage.WriteAsync(Guid.NewGuid(), 9, new byte[] { 1, 2, 3 });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void S3Storage_MissingConfigThrows()
    {
        var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Action act = () => new S3TenantExportStorage(
            new S3StorageOptions { Endpoint = "", Bucket = "b", AccessKey = "a", SecretKey = "s" },
            http, NullLogger<S3TenantExportStorage>.Instance);
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>Records every outgoing request and returns whatever the
    /// handler delegate produces. Lets us inspect the URI / method /
    /// headers from the test without a real HTTP listener.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<HttpRequestMessage> Requests { get; } = new();
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }
}
