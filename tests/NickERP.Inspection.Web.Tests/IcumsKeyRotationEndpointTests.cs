using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Database;
using NickERP.Inspection.Web.Endpoints;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Identity.Auth;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 9 / FU-icums-signing — exercises the rotation admin
/// endpoint surface at the handler level. The auth gate
/// (<see cref="IcumsKeyRotationEndpoint.AdminScope"/>) is enforced by
/// <c>RequireAuthorization()</c> at the routing layer in production;
/// the handler still falls back to a tenant-id check, and we assert
/// that a request with no tenant claim is rejected as 401.
/// </summary>
public sealed class IcumsKeyRotationEndpointTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Rotate_creates_a_new_inactive_key_and_returns_its_id()
    {
        await using var db = NewDb();
        var rotation = NewRotationService(db);

        var http = HttpForAdminTenant(tenantId: 1);
        var result = await IcumsKeyRotationEndpoint.RotateAsync(http, rotation);

        var resp = ExtractValue<RotateResponse>(result);
        resp.Should().NotBeNull();
        resp!.KeyId.Should().Be("k1");

        var stored = await db.IcumsSigningKeys.AsNoTracking().SingleAsync();
        stored.TenantId.Should().Be(1);
        stored.KeyId.Should().Be("k1");
        stored.ActivatedAt.Should().BeNull(because: "rotate creates the key in inactive state");
        stored.RetiredAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Rotate_increments_keyId_across_calls()
    {
        await using var db = NewDb();
        var rotation = NewRotationService(db);
        var http = HttpForAdminTenant(tenantId: 1);

        await IcumsKeyRotationEndpoint.RotateAsync(http, rotation);
        await IcumsKeyRotationEndpoint.RotateAsync(http, rotation);
        var third = await IcumsKeyRotationEndpoint.RotateAsync(http, rotation);

        ExtractValue<RotateResponse>(third)!.KeyId.Should().Be("k3");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Activate_transitions_states_correctly()
    {
        await using var db = NewDb();
        var rotation = NewRotationService(db);
        var http = HttpForAdminTenant(tenantId: 1);

        var first = await IcumsKeyRotationEndpoint.RotateAsync(http, rotation);
        var firstKeyId = ExtractValue<RotateResponse>(first)!.KeyId;

        // Activate the only key — there's no prior active to retire.
        var act1 = await IcumsKeyRotationEndpoint.ActivateAsync(
            http, rotation, new ActivateRequest(firstKeyId, VerificationWindowDays: 7));
        var activated1 = ExtractValue<ActivateResponse>(act1);
        activated1!.ActivatedKeyId.Should().Be(firstKeyId);
        activated1.RetiredKeyId.Should().BeNull();
        activated1.VerificationOnlyUntil.Should().BeNull();

        // Generate + activate a second key — first one must be retired.
        var second = await IcumsKeyRotationEndpoint.RotateAsync(http, rotation);
        var secondKeyId = ExtractValue<RotateResponse>(second)!.KeyId;
        var act2 = await IcumsKeyRotationEndpoint.ActivateAsync(
            http, rotation, new ActivateRequest(secondKeyId, VerificationWindowDays: 5));
        var activated2 = ExtractValue<ActivateResponse>(act2);

        activated2!.ActivatedKeyId.Should().Be(secondKeyId);
        activated2.RetiredKeyId.Should().Be(firstKeyId);
        activated2.VerificationOnlyUntil.Should().NotBeNull();
        activated2.VerificationOnlyUntil!.Value.Should()
            .BeAfter(DateTimeOffset.UtcNow.AddDays(4))
            .And.BeBefore(DateTimeOffset.UtcNow.AddDays(6));

        var fromDb = await db.IcumsSigningKeys.AsNoTracking().OrderBy(k => k.KeyId).ToListAsync();
        fromDb.Should().HaveCount(2);
        fromDb[0].RetiredAt.Should().NotBeNull();
        fromDb[0].VerificationOnlyUntil.Should().NotBeNull();
        fromDb[1].ActivatedAt.Should().NotBeNull();
        fromDb[1].RetiredAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Activate_returns_400_for_unknown_keyId()
    {
        await using var db = NewDb();
        var rotation = NewRotationService(db);
        var http = HttpForAdminTenant(tenantId: 1);

        var result = await IcumsKeyRotationEndpoint.ActivateAsync(
            http, rotation, new ActivateRequest("k99", VerificationWindowDays: 7));

        result.GetType().Name.Should().Contain("BadRequest");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task List_returns_keys_for_the_callers_tenant()
    {
        await using var db = NewDb();
        var rotation = NewRotationService(db);

        var httpA = HttpForAdminTenant(tenantId: 1);
        var httpB = HttpForAdminTenant(tenantId: 2);

        // Three keys for tenant 1, one for tenant 2. Without RLS in the
        // in-memory provider, tenant scoping is enforced by the LINQ
        // Where clause inside the rotation service.
        await IcumsKeyRotationEndpoint.RotateAsync(httpA, rotation);
        await IcumsKeyRotationEndpoint.RotateAsync(httpA, rotation);
        await IcumsKeyRotationEndpoint.RotateAsync(httpA, rotation);
        await IcumsKeyRotationEndpoint.RotateAsync(httpB, rotation);

        var listA = await IcumsKeyRotationEndpoint.ListAsync(httpA, rotation);
        var rowsA = ExtractValue<ListResponse>(listA);
        rowsA!.Keys.Should().HaveCount(3);
        rowsA.Keys.Select(k => k.KeyId).Should().BeEquivalentTo(new[] { "k1", "k2", "k3" });

        var listB = await IcumsKeyRotationEndpoint.ListAsync(httpB, rotation);
        var rowsB = ExtractValue<ListResponse>(listB);
        rowsB!.Keys.Should().HaveCount(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Endpoints_return_401_for_anonymous_caller_without_tenant_claim()
    {
        await using var db = NewDb();
        var rotation = NewRotationService(db);

        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };

        (await IcumsKeyRotationEndpoint.RotateAsync(http, rotation))
            .GetType().Name.Should().Be("UnauthorizedHttpResult");
        (await IcumsKeyRotationEndpoint.ListAsync(http, rotation))
            .GetType().Name.Should().Be("UnauthorizedHttpResult");
        (await IcumsKeyRotationEndpoint.ActivateAsync(http, rotation, new ActivateRequest("k1", null)))
            .GetType().Name.Should().Be("UnauthorizedHttpResult");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Mapped_routes_carry_authorization_requirement()
    {
        // Defence-in-depth: scan the IEndpointRouteBuilder data sources
        // after mapping the rotation endpoints, confirm IAuthorizeData is
        // present on each route. Mirrors the pattern from
        // WorkersHealthzEndpointTests so a future regression that drops
        // .RequireAuthorization() on the endpoint group fires here.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        // The minimal-API parameter-binding pipeline asks the DI graph
        // about each parameter type during InferMetadata. Register
        // enough scaffolding so the binding inference can resolve
        // IcumsKeyRotationService — we never actually invoke the
        // handler, but the inference walk requires the type to be
        // registered to classify it as a service-bound parameter.
        builder.Services.AddDbContext<InspectionDbContext>(o =>
            o.UseInMemoryDatabase("icums-rot-meta-" + Guid.NewGuid())
             .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        builder.Services.AddDataProtection();
        builder.Services.AddScoped<IcumsHmacEnvelopeSigner>();
        builder.Services.AddScoped<IcumsKeyRotationService>();

        var app = builder.Build();
        app.MapIcumsKeyRotationEndpoints();

        IEndpointRouteBuilder routeBuilder = app;
        var endpoints = routeBuilder.DataSources.SelectMany(ds => ds.Endpoints).ToList();

        var rotationEndpoints = endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty)
                .StartsWith("/api/icums/keys", StringComparison.OrdinalIgnoreCase))
            .ToList();

        rotationEndpoints.Should().NotBeEmpty(
            "the /api/icums/keys/* routes should be registered; enumerated: "
            + string.Join(", ", endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText)));

        foreach (var ep in rotationEndpoints)
        {
            ep.Metadata.GetMetadata<IAuthorizeData>().Should().NotBeNull(
                $"endpoint {ep.RoutePattern.RawText} must require authorization");
        }
    }

    // -- helpers --------------------------------------------------------

    private static InspectionDbContext NewDb()
    {
        var opts = new DbContextOptionsBuilder<InspectionDbContext>()
            .UseInMemoryDatabase("icums-rot-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new InspectionDbContext(opts);
    }

    private static IcumsKeyRotationService NewRotationService(InspectionDbContext db)
    {
        var dp = new EphemeralDataProtectionProvider();
        var signer = new IcumsHmacEnvelopeSigner(db, dp, NullLogger<IcumsHmacEnvelopeSigner>.Instance);
        return new IcumsKeyRotationService(db, signer, NullLogger<IcumsKeyRotationService>.Instance);
    }

    private static DefaultHttpContext HttpForAdminTenant(long tenantId)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(NickErpClaims.Id, Guid.NewGuid().ToString()));
        identity.AddClaim(new Claim(NickErpClaims.TenantId, tenantId.ToString()));
        identity.AddClaim(new Claim(System.Security.Claims.ClaimTypes.Role, IcumsKeyRotationEndpoint.AdminScope));
        return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
    }

    private static T? ExtractValue<T>(IResult result) where T : class
    {
        var prop = result.GetType().GetProperty("Value");
        return prop?.GetValue(result) as T;
    }
}
