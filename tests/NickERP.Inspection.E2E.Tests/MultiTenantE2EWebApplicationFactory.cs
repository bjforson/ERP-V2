using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NickERP.Inspection.E2E.Tests;

/// <summary>
/// Sprint E1 — multi-tenant variant of <see cref="E2EWebApplicationFactory"/>.
/// The D4 factory replaces <c>AuthenticationStateProvider</c> with a
/// single-tenant stub so the workflow service finds an actor without the
/// HTTP auth pipeline running. E1 needs to drive HTTP requests as
/// different users in different tenants — the real
/// <c>NickErpAuthenticationHandler</c> + <c>DbIdentityResolver</c> path
/// MUST be active so the principal's <c>nickerp:tenant_id</c> claim flows
/// from the resolved <see cref="Platform.Identity.Entities.IdentityUser"/>'s
/// <c>TenantId</c> column down through
/// <see cref="Platform.Tenancy.TenantResolutionMiddleware"/> into
/// <see cref="Platform.Tenancy.ITenantContext"/> and onto every Postgres
/// connection via the F1 interceptor.
///
/// <para>
/// What changes vs. D4:
/// <list type="bullet">
///   <item>No <c>AuthenticationStateProvider</c> override — the host's
///         default <c>ServerAuthenticationStateProvider</c> derives the
///         Blazor auth state from the HttpContext principal.</item>
///   <item>Boots with <c>nscim_app</c> connection strings (the post-H3
///         production posture). RLS actually filters because the role is
///         <c>NOSUPERUSER NOBYPASSRLS</c>.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class MultiTenantE2EWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _platformConnectionString;
    private readonly string _inspectionConnectionString;
    private readonly string _imagingStorageRoot;
    private readonly string _contentRoot;

    public MultiTenantE2EWebApplicationFactory(
        string platformConnectionString,
        string inspectionConnectionString,
        string imagingStorageRoot,
        string contentRoot)
    {
        _platformConnectionString = platformConnectionString;
        _inspectionConnectionString = inspectionConnectionString;
        _imagingStorageRoot = imagingStorageRoot;
        _contentRoot = contentRoot;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(_contentRoot);
        builder.UseEnvironment("Development");
        builder.UseUrls("http://127.0.0.1:0");

        builder.UseSetting("ConnectionStrings:Platform", _platformConnectionString);
        builder.UseSetting("ConnectionStrings:Inspection", _inspectionConnectionString);
        builder.UseSetting("NickErp:Inspection:Imaging:StorageRoot", _imagingStorageRoot);
        builder.UseSetting("NickErp:Inspection:Imaging:WorkerPollIntervalSeconds", "1");
        builder.UseSetting("NickErp:Inspection:Imaging:WorkerBatchSize", "8");
        builder.UseSetting("NickErp:Identity:CfAccess:TeamDomain", "nickscan-e2e");
        builder.UseSetting("NickErp:Identity:CfAccess:ApplicationAudience", "e2e-not-validated");
        builder.UseSetting("NickErp:Identity:CfAccess:DevBypass:Enabled", "true");
        // FakeUserEmail is the fall-back when the X-Dev-User header is
        // empty. The E1 test always sends an explicit header so this
        // value is never used; we still set it because Validate() requires it.
        builder.UseSetting("NickErp:Identity:CfAccess:DevBypass:FakeUserEmail", "unused-fallback@nickscan.com");
        // Migrations run as `postgres` (out of band, before the host
        // starts) — disable startup migrations so the host doesn't try
        // to migrate as `nscim_app`, which lacks DDL privileges on
        // `public` (per H3) and would fail.
        builder.UseSetting("RunMigrationsOnStartup", "false");
        builder.UseSetting(
            "NickErp:Logging:FileRoot",
            Path.Combine(Path.GetTempPath(), "nickerp-e2e-e1-logs"));
        builder.UseSetting("NickErp:Logging:SeqUrl", "http://localhost:65535");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("NickErp:Logging:MinimumLevel", "Warning");
    }
}
