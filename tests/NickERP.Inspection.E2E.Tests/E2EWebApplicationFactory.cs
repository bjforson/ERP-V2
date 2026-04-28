using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Boots the Inspection web host inside the test process. The factory
/// overrides:
/// <list type="bullet">
///   <item><c>ConnectionStrings:Platform</c> + <c>ConnectionStrings:Inspection</c>
///         to point at the test-only databases stood up by
///         <see cref="PostgresFixture"/>.</item>
///   <item><c>NickErp:Inspection:Imaging:StorageRoot</c> at a per-test
///         temp directory so the on-disk image store stays in scope.</item>
///   <item><c>NickErp:Identity:CfAccess</c> with stub TeamDomain /
///         ApplicationAudience values + dev-bypass enabled so the host's
///         <c>options.Validate()</c> doesn't throw at startup. We don't
///         exercise the dev-bypass HTTP path — the test calls
///         <see cref="Web.Services.CaseWorkflowService"/> directly via
///         <see cref="WebApplicationFactory{TEntryPoint}.Services"/> — but
///         the auth scheme registration would still fail without these.</item>
///   <item><see cref="AuthenticationStateProvider"/> with a stub that
///         returns <c>nickerp:tenant_id=1</c> + a stable <c>nickerp:id</c>
///         claim, so <see cref="Web.Services.CaseWorkflowService"/>
///         <c>CurrentActorAsync</c> can pull a non-null actor without
///         going through HTTP.</item>
/// </list>
/// The content root points at the test's build output directory so the
/// host's <c>Path.Combine(ContentRootPath, "plugins")</c> resolves to
/// the staged plugin folder produced by the csproj's
/// <c>StagePluginsForE2E</c> target.
/// </summary>
internal sealed class E2EWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _platformConnectionString;
    private readonly string _inspectionConnectionString;
    private readonly string _imagingStorageRoot;
    private readonly string _contentRoot;
    private readonly Guid _fakeUserId;

    public Guid FakeUserId => _fakeUserId;

    public E2EWebApplicationFactory(
        string platformConnectionString,
        string inspectionConnectionString,
        string imagingStorageRoot,
        string contentRoot,
        Guid fakeUserId)
    {
        _platformConnectionString = platformConnectionString;
        _inspectionConnectionString = inspectionConnectionString;
        _imagingStorageRoot = imagingStorageRoot;
        _contentRoot = contentRoot;
        _fakeUserId = fakeUserId;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // ContentRoot drives where the host looks for the plugins folder.
        // Our csproj target stages plugin DLLs + manifests into
        // {OutputPath}/plugins/, which is what TestContext-style
        // resolution lands on at runtime.
        builder.UseContentRoot(_contentRoot);

        // Force Development so the dev-bypass option is allowed (the
        // platform throws otherwise). Also matches how the app actually
        // runs when an analyst pokes it locally.
        builder.UseEnvironment("Development");

        // Pick a sentinel port that's not 5410 (the live host's). The
        // factory only binds when CreateClient is called; the test
        // mostly drives the workflow service through Services so this
        // is mainly defensive.
        builder.UseUrls("http://127.0.0.1:0");

        // UseSetting writes into the host-builder's settings dictionary
        // BEFORE Program.cs's top-level code executes — so when Program
        // does `builder.Configuration.GetConnectionString("Platform")`,
        // it reads our test value, not the appsettings.json sentinel
        // ("__OVERRIDE_VIA_USER_SECRETS_OR_ENV__"). ConfigureAppConfiguration
        // would arrive too late: WebApplicationFactory builds the host
        // by running Program.<Main>$() and the AddDbContext registration
        // captures the connection string at that point.
        builder.UseSetting("ConnectionStrings:Platform", _platformConnectionString);
        builder.UseSetting("ConnectionStrings:Inspection", _inspectionConnectionString);
        builder.UseSetting("NickErp:Inspection:Imaging:StorageRoot", _imagingStorageRoot);
        builder.UseSetting("NickErp:Inspection:Imaging:WorkerPollIntervalSeconds", "1");
        builder.UseSetting("NickErp:Inspection:Imaging:WorkerBatchSize", "8");
        builder.UseSetting("NickErp:Identity:CfAccess:TeamDomain", "nickscan-e2e");
        builder.UseSetting("NickErp:Identity:CfAccess:ApplicationAudience", "e2e-not-validated");
        builder.UseSetting("NickErp:Identity:CfAccess:DevBypass:Enabled", "true");
        builder.UseSetting("NickErp:Identity:CfAccess:DevBypass:FakeUserEmail", "dev@nickscan.com");
        builder.UseSetting("RunMigrationsOnStartup", "true");
        builder.UseSetting(
            "NickErp:Logging:FileRoot",
            Path.Combine(Path.GetTempPath(), "nickerp-e2e-logs"));
        // Unreachable Seq sink so log emission doesn't slow the test
        // down. Serilog absorbs sink-write failures internally.
        builder.UseSetting("NickErp:Logging:SeqUrl", "http://localhost:65535");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("NickErp:Logging:MinimumLevel", "Warning");

        builder.ConfigureServices(services =>
        {
            // Replace the real AuthenticationStateProvider with a stub
            // that returns a stable principal carrying the tenant + id
            // claims CaseWorkflowService.CurrentActorAsync expects. The
            // test never hits an actual HTTP endpoint that requires auth;
            // the principal is consumed by the workflow service alone.
            services.RemoveAll<AuthenticationStateProvider>();
            services.AddScoped<AuthenticationStateProvider>(_ =>
                new StubAuthStateProvider(_fakeUserId, tenantId: 1));

            // H1 — the ITenantContext stub used to live here as a
            // single-tenant fixture override that papered over
            // PreRenderWorker / SourceJanitorWorker not setting the
            // tenant in their per-cycle scopes. After H1 both workers
            // walk tenancy.tenants and call ITenantContext.SetTenant
            // themselves, mirroring ScannerIngestionWorker, so the
            // production tenancy registration is now the right thing
            // for the e2e test to exercise. No stub needed.
        });
    }

    /// <summary>
    /// Helper for the test: open a service scope, push tenant=1 onto
    /// <see cref="ITenantContext"/> (the host's tenancy middleware only
    /// runs on real HTTP requests, not on direct service-provider calls),
    /// and hand back the scope so the test can resolve scoped services.
    /// </summary>
    public IServiceScope CreateTenantScope(long tenantId = 1)
    {
        var scope = Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (!tenant.IsResolved || tenant.TenantId != tenantId)
            tenant.SetTenant(tenantId);
        return scope;
    }

    private sealed class StubAuthStateProvider : AuthenticationStateProvider
    {
        private readonly Guid _userId;
        private readonly long _tenantId;

        public StubAuthStateProvider(Guid userId, long tenantId)
        {
            _userId = userId;
            _tenantId = tenantId;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "e2e-test"),
                new Claim("nickerp:id", _userId.ToString()),
                new Claim("nickerp:tenant_id", _tenantId.ToString()),
            }, "E2E");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
