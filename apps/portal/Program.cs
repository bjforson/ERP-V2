using Microsoft.AspNetCore.Authorization;
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

var builder = WebApplication.CreateBuilder(args);

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
                migrateLogger.LogInformation("Migrations applied for Identity, Tenancy, Audit, NickFinance.");
            }
            else
            {
                migrateLogger.LogInformation("Migrations applied for Identity, Tenancy, Audit. NickFinance not configured.");
            }
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
