using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Inspection.Edge.Abstractions;
using NickERP.Inspection.Web.Endpoints;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 45 / Phase E — coverage for the
/// <see cref="EdgeReplayEndpoint.ScanPackageHint"/> dispatch path.
/// Drives the manifest sha256 + HMAC signature validation, the
/// scan_artifact persistence, and the audit-event emission for both
/// success and failure paths.
/// </summary>
public sealed class EdgeReplayManifestValidationTests
{
    private const long TenantId = 17;
    private const string EdgeNodeId = "edge-tema-1";

    [Fact]
    public async Task ScanPackage_replay_with_valid_manifest_succeeds_and_persists_artifact()
    {
        var config = EdgeReplayEndpointTests.BuildConfig();
        var scaffold = BuildScaffold("manifest-ok", config);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(scaffold.AuditDb, config);
        var (apiKey, http) = await SeedPerNodeKeyAsync(scaffold.AuditDb, config);

        var package = SealRoundTrippedPackage(System.Text.Encoding.UTF8.GetBytes(apiKey), out _);
        var payload = SerialiseToWireDto(package);

        var body = new EdgeReplayRequestDto(EdgeNodeId, new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScanPackageHint, TenantId, DateTimeOffset.UtcNow.AddMinutes(-1), payload)
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, scaffold.AuditDb, new TenantContext(), auth, NullLoggerFactory.Instance, body,
            clock: null, services: scaffold.Services);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeTrue();

        // Re-resolve the inspection DbContext from the SP so we read
        // through a non-disposed handle on the same shared store.
        using var readScope = scaffold.Services.CreateScope();
        var inspRead = readScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        var artifacts = await inspRead.ScanArtifacts.AsNoTracking().ToListAsync();
        artifacts.Should().HaveCount(1);
        var art = artifacts[0];
        art.ManifestSha256.Should().NotBeNull().And.HaveCount(32);
        art.ManifestSignature.Should().NotBeNull().And.HaveCount(32);
        art.ManifestVerifiedAt.Should().NotBeNull();
        art.ManifestJson.Should().NotBeNullOrEmpty();
        art.ContentHash.Should().Be(package.ImageFiles[0].Sha256Hex);

        var auditRead = readScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var auditRow = await auditRead.Events.AsNoTracking()
            .SingleAsync(e => e.EventType == EdgeReplayEndpoint.ManifestValidatedAudit);
        auditRow.EntityType.Should().Be("ScanPackage");
        auditRow.EntityId.Should().Be(package.ScanId);
    }

    [Fact]
    public async Task ScanPackage_replay_with_tampered_manifest_fails_and_emits_failure_audit()
    {
        var config = EdgeReplayEndpointTests.BuildConfig();
        var scaffold = BuildScaffold("manifest-tamper", config);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(scaffold.AuditDb, config);
        var (apiKey, http) = await SeedPerNodeKeyAsync(scaffold.AuditDb, config);

        var package = SealRoundTrippedPackage(System.Text.Encoding.UTF8.GetBytes(apiKey), out _);
        var tamperedPackage = package with { SiteId = "DIFFERENT" };
        var payload = SerialiseToWireDto(tamperedPackage);

        var body = new EdgeReplayRequestDto(EdgeNodeId, new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScanPackageHint, TenantId, DateTimeOffset.UtcNow.AddMinutes(-1), payload)
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, scaffold.AuditDb, new TenantContext(), auth, NullLoggerFactory.Instance, body,
            clock: null, services: scaffold.Services);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("manifest validation failed");

        using var readScope = scaffold.Services.CreateScope();
        var inspRead = readScope.ServiceProvider.GetRequiredService<InspectionDbContext>();
        (await inspRead.ScanArtifacts.AsNoTracking().CountAsync()).Should().Be(0);

        var auditRead = readScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var auditRow = await auditRead.Events.AsNoTracking()
            .SingleAsync(e => e.EventType == EdgeReplayEndpoint.ManifestValidationFailedAudit);
        auditRow.EntityType.Should().Be("ScanPackage");
    }

    [Fact]
    public async Task ScanPackage_replay_with_image_byte_tamper_fails_image_sha256_check()
    {
        var config = EdgeReplayEndpointTests.BuildConfig();
        var scaffold = BuildScaffold("manifest-img-tamper", config);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(scaffold.AuditDb, config);
        var (apiKey, http) = await SeedPerNodeKeyAsync(scaffold.AuditDb, config);

        var package = SealRoundTrippedPackage(System.Text.Encoding.UTF8.GetBytes(apiKey), out _);
        var tamperedBytes = (byte[])package.ImageFiles[0].Data.Clone();
        tamperedBytes[0] ^= 0xFF;
        var tamperedFile = package.ImageFiles[0] with { Data = tamperedBytes };
        var tampered = package with { ImageFiles = new[] { tamperedFile } };
        var payload = SerialiseToWireDto(tampered);

        var body = new EdgeReplayRequestDto(EdgeNodeId, new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScanPackageHint, TenantId, DateTimeOffset.UtcNow.AddMinutes(-1), payload)
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, scaffold.AuditDb, new TenantContext(), auth, NullLoggerFactory.Instance, body,
            clock: null, services: scaffold.Services);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("ImageSha256Mismatch");
    }

    [Fact]
    public async Task ScanPackage_replay_with_legacy_token_auth_rejected()
    {
        var config = EdgeReplayEndpointTests.BuildConfig(serverSecret: "legacy-secret", allowLegacy: true);
        var scaffold = BuildScaffold("manifest-legacy", config);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(scaffold.AuditDb, config);

        scaffold.AuditDb.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = EdgeNodeId, TenantId = TenantId, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await scaffold.AuditDb.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Token"] = "legacy-secret";

        var dummyKey = System.Text.Encoding.UTF8.GetBytes("dummy-key-32-bytes-fixed-padding");
        var package = SealRoundTrippedPackage(dummyKey, out _);
        var payload = SerialiseToWireDto(package);

        var body = new EdgeReplayRequestDto(EdgeNodeId, new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScanPackageHint, TenantId, DateTimeOffset.UtcNow.AddMinutes(-1), payload)
        });

        var result = await EdgeReplayEndpoint.HandleAsync(
            http, scaffold.AuditDb, new TenantContext(), auth, NullLoggerFactory.Instance, body,
            clock: null, services: scaffold.Services);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("per-edge");
    }

    [Fact]
    public async Task ScanPackage_replay_without_services_returns_per_entry_error()
    {
        var config = EdgeReplayEndpointTests.BuildConfig();
        var scaffold = BuildScaffold("manifest-no-svc", config);
        var auth = EdgeReplayEndpointTests.BuildAuthHandler(scaffold.AuditDb, config);
        var (_, http) = await SeedPerNodeKeyAsync(scaffold.AuditDb, config);

        var dummyPayload = JsonSerializer.SerializeToElement(new { scanId = "x" });
        var body = new EdgeReplayRequestDto(EdgeNodeId, new List<EdgeReplayEventDto>
        {
            new(EdgeReplayEndpoint.ScanPackageHint, TenantId, DateTimeOffset.UtcNow.AddMinutes(-1), dummyPayload)
        });

        // No services arg — endpoint should fail-fast with a per-entry error.
        var result = await EdgeReplayEndpoint.HandleAsync(
            http, scaffold.AuditDb, new TenantContext(), auth, NullLoggerFactory.Instance, body);

        var dto = EdgeReplayEndpointTests.ExtractValue<EdgeReplayResponseDto>(result);
        dto!.Results.Single().Ok.Should().BeFalse();
        dto.Results.Single().Error.Should().Contain("DI provider");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Test scaffold: build an InspectionDbContext + AuditDbContext +
    /// IServiceProvider that point at the same InMemory-EF stores. The
    /// scaffold pre-creates the outer test contexts (factory call) so
    /// the test reads/writes via stable handles; the SP uses
    /// scoped-factory delegates that hand out FRESH contexts on every
    /// CreateScope() so the worker's per-cycle scope-disposal can't
    /// take down the outer test's handles.
    /// </summary>
    private static TestScaffold BuildScaffold(string name, IConfiguration config)
    {
        var auditDbName = "audit-" + name + "-" + Guid.NewGuid();
        var inspDbName = "insp-" + name + "-" + Guid.NewGuid();

        Func<AuditDbContext> auditFactory = () =>
        {
            var opts = new DbContextOptionsBuilder<AuditDbContext>()
                .UseInMemoryDatabase(auditDbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return EdgeReplayEndpointTests.BuildAuditDbWithName(auditDbName);
        };
        Func<InspectionDbContext> inspFactory = () =>
        {
            var opts = new DbContextOptionsBuilder<InspectionDbContext>()
                .UseInMemoryDatabase(inspDbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            return new InspectionDbContext(opts);
        };

        var auditDb = auditFactory();
        var inspDb = inspFactory();

        var sc = new ServiceCollection();
        // Use factory registrations rather than AddDbContext so we can
        // hand out the production-test AuditDbContext subclass with
        // each scope. The InMemory provider is shared via the named
        // database so all instances see the same backing store.
        sc.AddScoped(_ => auditFactory());
        sc.AddScoped(_ => inspFactory());
        sc.AddScoped<ITenantContext, TenantContext>();
        sc.AddSingleton(config);
        var sp = sc.BuildServiceProvider();

        return new TestScaffold(auditDb, inspDb, sp);
    }

    private sealed record TestScaffold(
        AuditDbContext AuditDb, InspectionDbContext InspDb, IServiceProvider Services);

    private static async Task<(string ApiKey, HttpContext Http)> SeedPerNodeKeyAsync(
        AuditDbContext db, IConfiguration config)
    {
        var hasher = new EdgeKeyHasher(config, new TestEdgeKeyHashEnvelope());
        var plaintext = EdgeKeyHasher.GenerateApiKey();
        db.EdgeNodeApiKeys.Add(new EdgeNodeApiKey
        {
            TenantId = TenantId,
            EdgeNodeId = EdgeNodeId,
            KeyHash = hasher.ComputeHash(plaintext),
            KeyPrefix = EdgeKeyHasher.ComputePrefix(plaintext),
            IssuedAt = DateTimeOffset.UtcNow
        });
        db.EdgeNodeAuthorizations.Add(new EdgeNodeAuthorization
        {
            EdgeNodeId = EdgeNodeId, TenantId = TenantId, AuthorizedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext();
        http.Request.Headers["X-Edge-Api-Key"] = plaintext;
        return (plaintext, http);
    }

    private static ScanPackage SealRoundTrippedPackage(byte[] hmacKey, out byte[] imageBytes)
    {
        imageBytes = new byte[64];
        new Random(42).NextBytes(imageBytes);
        var sha = ScanPackageManifest.Sha256Hex(imageBytes);
        var image = new ImageFile(
            FileName: "primary.img",
            Sha256Hex: sha,
            View: "primary",
            SizeBytes: imageBytes.Length,
            Data: imageBytes);

        var pkg = new ScanPackage(
            ScanId: Guid.NewGuid().ToString(),
            SiteId: "TKD",
            ScannerId: "fs6000",
            GatewayId: EdgeNodeId,
            ScanType: "primary",
            OccurredAt: DateTimeOffset.UtcNow.AddMinutes(-2),
            OperatorId: string.Empty,
            ContainerNumber: "MSCU1234567",
            VehiclePlate: string.Empty,
            DeclarationNumber: string.Empty,
            ManifestNumber: string.Empty,
            ImageFiles: new[] { image },
            ManifestSha256: Array.Empty<byte>(),
            ManifestSignature: Array.Empty<byte>());
        return ScanPackageManifest.Seal(pkg, hmacKey);
    }

    private static JsonElement SerialiseToWireDto(ScanPackage package)
    {
        var dto = new ScanPackageWireDto
        {
            ScanId = package.ScanId,
            SiteId = package.SiteId,
            ScannerId = package.ScannerId,
            GatewayId = package.GatewayId,
            ScanType = package.ScanType,
            OccurredAt = package.OccurredAt,
            OperatorId = package.OperatorId,
            ContainerNumber = package.ContainerNumber,
            VehiclePlate = package.VehiclePlate,
            DeclarationNumber = package.DeclarationNumber,
            ManifestNumber = package.ManifestNumber,
            ImageFiles = package.ImageFiles.Select(f => new ImageFileWireDto
            {
                FileName = f.FileName,
                Sha256Hex = f.Sha256Hex,
                View = f.View,
                SizeBytes = f.SizeBytes,
                Data = Convert.ToBase64String(f.Data)
            }).ToList(),
            ManifestSha256 = Convert.ToBase64String(package.ManifestSha256),
            ManifestSignature = Convert.ToBase64String(package.ManifestSignature)
        };
        // The wire DTO is snake-case on the wire — match the
        // internal options the endpoint uses.
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        return JsonSerializer.SerializeToElement(dto, opts);
    }
}
