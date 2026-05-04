using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Identity;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Logging;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Portal.Components;
// G2 — NickFinance optional registration.
using NickERP.NickFinance.Database;
using NickERP.NickFinance.Web;
using NickERP.NickFinance.Web.Endpoints;
// Sprint 14 / VP6 Phase A.5 — IAnalysisServiceBootstrap for Tenants.razor.
using NickERP.Inspection.Application.AnalysisServices;
using NickERP.Inspection.Database;

var builder = WebApplication.CreateBuilder(args);

// Windows Service host integration — required so SCM "service started"
// signaling works when this binary is hosted as the NSCIM_Portal service.
// No-op when running interactively (dotnet run / direct .exe invocation).
builder.Host.UseWindowsService();

// ---------------------------------------------------------------------------
// Track A — observability foundation. Logs to Seq, traces + metrics over OTLP.
// ---------------------------------------------------------------------------
builder.UseNickErpLogging("NickERP.Portal");
builder.UseNickErpTelemetry("NickERP.Portal");

// ---------------------------------------------------------------------------
// Track A — Identity (auth scheme + DB-backed resolver) + Tenancy + Audit.
// All three share the nickerp_platform DB; one connection string from
// ConnectionStrings:Platform.
// ---------------------------------------------------------------------------
var platformConn = builder.Configuration.GetConnectionString("Platform");

builder.Services.AddNickErpIdentity(builder.Configuration, builder.Environment);
builder.Services.AddNickErpIdentityCore(platformConn);
builder.Services.AddNickErpTenancy();
builder.Services.AddNickErpTenancyCore(platformConn);
builder.Services.AddNickErpAuditCore(platformConn);
// Sprint 18 — tenant lifecycle admin (suspend / soft-delete / hard-purge).
// Reads downstream connection strings from env vars at runtime (the
// orchestrator opens its own connections to nickerp_inspection /
// nickerp_nickfinance for the cross-DB cascade).
builder.Services.AddNickErpTenantLifecycle(opts =>
{
    // Prefer config-bound connection strings when present; fall back to
    // the env-var defaults the AddNickErpTenantLifecycle helper sets up.
    if (!string.IsNullOrWhiteSpace(platformConn))
    {
        opts.PlatformConnectionString = platformConn;
    }
});

// ---------------------------------------------------------------------------
// G2 — NickFinance Petty Cash pathfinder. Optional: AddNickErpNickFinanceWeb
// returns the resolved connection string (or empty string if
// ConnectionStrings:NickFinance is unset). When empty, the module simply
// isn't registered and the sidenav link below is hidden — see G2 §11.
// ---------------------------------------------------------------------------
var nickFinanceConn = builder.Services.AddNickErpNickFinanceWeb(builder.Configuration);
var nickFinanceEnabled = !string.IsNullOrWhiteSpace(nickFinanceConn);
// Exposed to Razor pages (sidenav) so a deployment without NickFinance
// can hide the link.
builder.Services.AddSingleton(new NickERP.Portal.Services.NickFinanceFeatureFlag(nickFinanceEnabled));

// ---------------------------------------------------------------------------
// Sprint 14 / VP6 Phase A.5 — InspectionDbContext + IAnalysisServiceBootstrap.
// Tenants.razor calls EnsureAllLocationsServiceAsync after creating a new
// tenant so the immutable un-deletable "All Locations" AnalysisService
// exists for it. The bootstrap writes through InspectionDbContext, which
// must be wired with the same tenancy interceptors as Inspection.Web so
// the TenantId stamp + RLS policy on inspection.analysis_services pass.
//
// ConnectionStrings:Inspection is OPTIONAL on this host — when absent,
// the bootstrap is not registered and Tenants.razor skips the call. This
// matches the G2 NickFinance pattern (config-gated optional module).
// ---------------------------------------------------------------------------
var inspectionConn = builder.Configuration.GetConnectionString("Inspection");
var inspectionEnabled = !string.IsNullOrWhiteSpace(inspectionConn);
if (inspectionEnabled)
{
    builder.Services.AddDbContext<InspectionDbContext>((sp, opts) =>
    {
        opts.UseNpgsql(inspectionConn,
            npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(InspectionDbContext).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "inspection");
            });
        opts.AddInterceptors(
            sp.GetRequiredService<TenantConnectionInterceptor>(),
            sp.GetRequiredService<TenantOwnedEntityInterceptor>());
    });
    builder.Services.AddAnalysisServiceBootstrap();
}
builder.Services.AddSingleton(new NickERP.Portal.Services.InspectionFeatureFlag(inspectionEnabled));

// Default authorization policy: every endpoint or component requires auth.
builder.Services.AddAuthorization(opts =>
{
    opts.DefaultPolicy = new AuthorizationPolicyBuilder(CfAccessAuthenticationOptions.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpClient(); // for Health page service probes

// Sprint tracker — reads docs/sprint-progress.json (the canonical state file)
// for the /sprint page. No DB write, no auth secret; safe singleton.
builder.Services.AddSingleton<NickERP.Portal.Services.SprintProgressService>();

// Sprint 13 / P2-FU-edge-auth — wiring for the per-edge-node API key
// admin page (/edge-keys). The hasher + envelope + service live in
// Platform.Audit.Database alongside the entity so they don't drag a
// host-side dependency. Portal needs AddDataProtection to resolve the
// envelope's IDataProtectionProvider (auth cookie infra usually wires
// this implicitly, but the explicit call documents the dependency
// and gives a stable application name for keyring isolation).
builder.Services.AddDataProtection().SetApplicationName("NickERP.Portal");
builder.Services.AddSingleton<NickERP.Platform.Audit.Database.IEdgeKeyHashEnvelope,
    NickERP.Portal.Services.PortalEdgeKeyHashEnvelope>();
builder.Services.AddScoped<NickERP.Platform.Audit.Database.EdgeKeyHasher>();
builder.Services.AddScoped<NickERP.Platform.Audit.Database.EdgeNodeApiKeyService>();

// Blazor Server (interactive server-side rendering).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---------------------------------------------------------------------------
// Phase F5 — health checks. /healthz/live is unconditional; /healthz/ready
// runs a SELECT 1 against each platform DbContext the Portal owns. Tiny
// custom probe rather than AspNetCore.HealthChecks.NpgSql to avoid pulling
// a third-party package whose .NET 10 release we'd have to verify, and to
// match the Inspection host's shape exactly.
// ---------------------------------------------------------------------------
builder.Services.AddScoped<PortalDbHealthCheck<IdentityDbContext>>(sp =>
    new PortalDbHealthCheck<IdentityDbContext>(sp.GetRequiredService<IdentityDbContext>(), "Postgres (identity)"));
builder.Services.AddScoped<PortalDbHealthCheck<TenancyDbContext>>(sp =>
    new PortalDbHealthCheck<TenancyDbContext>(sp.GetRequiredService<TenancyDbContext>(), "Postgres (tenancy)"));
builder.Services.AddScoped<PortalDbHealthCheck<AuditDbContext>>(sp =>
    new PortalDbHealthCheck<AuditDbContext>(sp.GetRequiredService<AuditDbContext>(), "Postgres (audit)"));

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("process running"), tags: new[] { "live" })
    .AddCheck<PortalDbHealthCheck<IdentityDbContext>>("postgres-identity", tags: new[] { "ready" })
    .AddCheck<PortalDbHealthCheck<TenancyDbContext>>("postgres-tenancy", tags: new[] { "ready" })
    .AddCheck<PortalDbHealthCheck<AuditDbContext>>("postgres-audit", tags: new[] { "ready" });

var app = builder.Build();

// ---------------------------------------------------------------------------
// Phase F5 — migrations at startup for the platform DBs the Portal owns
// (Identity + Tenancy + Audit, all on nickerp_platform). Default ON in dev,
// OFF elsewhere. RunMigrationsOnStartup overrides per environment.
// ---------------------------------------------------------------------------
{
    var runMigrations = app.Configuration.GetValue<bool?>("RunMigrationsOnStartup")
        ?? app.Environment.IsDevelopment();
    if (runMigrations)
    {
        using var migrationScope = app.Services.CreateScope();
        var sp = migrationScope.ServiceProvider;
        var migrateLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Migrations");
        try
        {
            sp.GetRequiredService<IdentityDbContext>().Database.Migrate();
            sp.GetRequiredService<TenancyDbContext>().Database.Migrate();
            sp.GetRequiredService<AuditDbContext>().Database.Migrate();
            // G2 — apply NickFinance migrations only when the module is
            // wired (connection string present). Skips silently otherwise.
            if (nickFinanceEnabled)
            {
                sp.GetRequiredService<NickFinanceDbContext>().Database.Migrate();
            }
            // Sprint 14 / VP6 Phase A.5 — apply Inspection migrations
            // only when ConnectionStrings:Inspection is set on this host.
            // The Inspection.Web service applies these too; running them
            // here is a belt-and-suspenders for portal-only deploys that
            // need the analysis_services tables before tenant creates fire.
            if (inspectionEnabled)
            {
                sp.GetRequiredService<InspectionDbContext>().Database.Migrate();
            }
            migrateLogger.LogInformation(
                "Migrations applied for Identity, Tenancy, Audit{NickFinance}{Inspection}.",
                nickFinanceEnabled ? ", NickFinance" : string.Empty,
                inspectionEnabled ? ", Inspection" : string.Empty);
        }
        catch (Exception ex)
        {
            migrateLogger.LogCritical(ex, "Migration at startup failed; aborting host bootstrap.");
            throw;
        }
    }
    else
    {
        var noMigrateLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup.Migrations");
        noMigrateLogger.LogInformation("RunMigrationsOnStartup=false; skipping Database.Migrate().");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseStaticFiles();
app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();
app.UseNickErpTenancy(); // resolves nickerp:tenant_id claim into ITenantContext

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    // G2 — register NickFinance Razor pages by their assembly so the
    // Router in Components/Routes.razor can pick up the @page routes.
    // The pages live in modules/nickfinance/.../Components/Pages and
    // are referenced via the NickERP.NickFinance.Web project.
    .AddAdditionalAssemblies(typeof(NickERP.NickFinance.Web.NickFinanceWebServiceCollectionExtensions).Assembly);

// G2 — petty-cash / fx-rates / periods minimal-API endpoints. Only
// mapped when the module is configured for this host.
if (nickFinanceEnabled)
{
    app.MapPettyCashEndpoints();
}

// ---------------------------------------------------------------------------
// Phase F5 — health endpoints. Anonymous (probe traffic shouldn't carry
// credentials). /healthz/live = process up; /healthz/ready = full probe.
// ---------------------------------------------------------------------------
app.MapHealthChecks("/healthz/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live")
}).AllowAnonymous();

app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                error = e.Value.Exception?.Message
            })
        };
        await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body, payload);
    }
}).AllowAnonymous();

app.Run();

/// <summary>
/// Phase F5 — readiness probe for a Postgres-backed platform DbContext.
/// Local to the Portal host (mirrors the analogous helper inside the
/// Inspection.Web project). Runs <c>SELECT 1</c> via
/// <c>DatabaseFacade.ExecuteSqlRawAsync</c>; returns
/// <c>HealthStatus.Unhealthy</c> on any failure with the inner exception
/// captured.
/// </summary>
internal sealed class PortalDbHealthCheck<TDbContext> : IHealthCheck
    where TDbContext : DbContext
{
    private readonly TDbContext _db;
    private readonly string _name;

    public PortalDbHealthCheck(TDbContext db, string name)
    {
        _db = db;
        _name = name;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy($"{_name} reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                description: $"{_name} unreachable: {ex.GetType().Name}",
                exception: ex);
        }
    }
}
