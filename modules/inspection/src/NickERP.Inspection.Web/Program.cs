using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NickERP.Inspection.Database;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Components;
using NickERP.Inspection.Web.HealthChecks;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Identity;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Logging;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Track A — observability (Logging + Telemetry).
// ---------------------------------------------------------------------------
builder.UseNickErpLogging("NickERP.Inspection.Web");
builder.UseNickErpTelemetry("NickERP.Inspection.Web");

// ---------------------------------------------------------------------------
// Track A — Identity (auth + DB-backed resolver), Tenancy, Audit (events).
// All three live in the platform DB; inspection has its own DB.
// ---------------------------------------------------------------------------
var platformConn = builder.Configuration.GetConnectionString("Platform");
var inspectionConn = builder.Configuration.GetConnectionString("Inspection");

builder.Services.AddNickErpIdentity(builder.Configuration, builder.Environment);
builder.Services.AddNickErpIdentityCore(platformConn);
builder.Services.AddNickErpTenancy();
// D2 — TenancyDbContext registered so the ScannerIngestionWorker can
// enumerate active tenants for cross-tenant scanner discovery
// (`tenancy.tenants` is the only table not under RLS).
builder.Services.AddNickErpTenancyCore(platformConn);
builder.Services.AddNickErpAuditCore(platformConn);

// Inspection's own DbContext. Phase F1 — wires the tenancy interceptors
// (push app.tenant_id to Postgres for RLS + stamp TenantId on inserts).
builder.Services.AddDbContext<InspectionDbContext>((sp, opts) =>
{
    opts.UseNpgsql(inspectionConn ?? throw new InvalidOperationException(
        "ConnectionStrings:Inspection is required (the nickerp_inspection Postgres DB)."),
        npgsql => npgsql.MigrationsAssembly(typeof(InspectionDbContext).Assembly.GetName().Name));
    opts.AddInterceptors(
        sp.GetRequiredService<TenantConnectionInterceptor>(),
        sp.GetRequiredService<TenantOwnedEntityInterceptor>());
});

// ---------------------------------------------------------------------------
// Track A — Plugins. Loads adapter DLLs from {ContentRoot}/plugins. The
// inspection module's plugin contracts (IScannerAdapter, etc.) are
// implemented by adapters that drop into this folder.
// ---------------------------------------------------------------------------
var pluginsDir = Path.Combine(builder.Environment.ContentRootPath, "plugins");
builder.Services.AddNickErpPluginsEager(pluginsDir);

// ---------------------------------------------------------------------------
// Auth + Razor Components.
// ---------------------------------------------------------------------------
builder.Services.AddAuthorization(opts =>
{
    opts.DefaultPolicy = new AuthorizationPolicyBuilder(CfAccessAuthenticationOptions.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

// Case workflow orchestration — single place where state transitions +
// DomainEvent emission happen. Pages call this; pages don't open the
// DbContext directly for workflow operations.
builder.Services.AddScoped<NickERP.Inspection.Web.Services.CaseWorkflowService>();

// Image pre-rendering pipeline (ARCHITECTURE §7.7) — IImageRenderer +
// IImageStore + the PreRenderWorker background service. The render
// endpoint is mapped below after app.Build().
builder.Services.AddNickErpImaging(builder.Configuration);

// D2 — ScannerIngestionWorker: drives every active ScannerDeviceInstance
// through IScannerAdapter.StreamAsync and creates/reuses a case for each
// emitted artifact. Closes the demo loop so dropping a real FS6000
// triplet into a watch folder produces a case end-to-end without a
// button click.
builder.Services.AddHostedService<NickERP.Inspection.Web.Services.ScannerIngestionWorker>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ---------------------------------------------------------------------------
// Phase F5 — health checks. /healthz/live is unconditional; /healthz/ready
// asserts every dependency the host needs to serve a real request:
//   - Postgres reachable on nickerp_platform (Identity + Audit DBs)
//   - Postgres reachable on nickerp_inspection
//   - Plugin registry populated (≥ 1 plugin loaded)
//   - ImagingOptions.StorageRoot writable
// Tag-filtered endpoints keep the live probe cheap.
// ---------------------------------------------------------------------------
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("process running"), tags: new[] { "live" })
    .AddCheck<PostgresHealthCheck<IdentityDbContext>>("postgres-platform-identity", tags: new[] { "ready" })
    .AddCheck<PostgresHealthCheck<NickERP.Platform.Audit.Database.AuditDbContext>>("postgres-platform-audit", tags: new[] { "ready" })
    .AddCheck<PostgresHealthCheck<InspectionDbContext>>("postgres-inspection", tags: new[] { "ready" })
    .AddCheck<PluginRegistryHealthCheck>("plugin-registry", tags: new[] { "ready" })
    .AddCheck<ImagingStorageHealthCheck>("imaging-storage", tags: new[] { "ready" });

// The DbContext-typed checks are resolved through DI — register them as
// scoped so each probe gets a fresh DbContext (Postgres connection
// pooling means this is cheap; matches how Razor pages consume it).
builder.Services.AddScoped(sp => new PostgresHealthCheck<IdentityDbContext>(
    sp.GetRequiredService<IdentityDbContext>(), "Postgres (identity)"));
builder.Services.AddScoped(sp => new PostgresHealthCheck<NickERP.Platform.Audit.Database.AuditDbContext>(
    sp.GetRequiredService<NickERP.Platform.Audit.Database.AuditDbContext>(), "Postgres (audit)"));
builder.Services.AddScoped(sp => new PostgresHealthCheck<InspectionDbContext>(
    sp.GetRequiredService<InspectionDbContext>(), "Postgres (inspection)"));

var app = builder.Build();

// ---------------------------------------------------------------------------
// Phase F5 — migrations at startup. Default ON in dev, OFF elsewhere; flag
// is RunMigrationsOnStartup so ops can override per environment without
// rebuilding. The migration applier runs synchronously inside the host's
// startup so a bad migration fails the boot rather than hiding behind a
// silent first-request error.
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
            sp.GetRequiredService<NickERP.Platform.Audit.Database.AuditDbContext>().Database.Migrate();
            // D2 — TenancyDbContext is registered (used by the
            // ScannerIngestionWorker for cross-tenant discovery); apply
            // its migrations at startup so the worker doesn't hit a
            // missing-table on first poll if Inspection boots before
            // the portal app has had a chance to seed.
            sp.GetRequiredService<TenancyDbContext>().Database.Migrate();
            sp.GetRequiredService<InspectionDbContext>().Database.Migrate();
            migrateLogger.LogInformation("Migrations applied for Identity, Audit, Tenancy, Inspection.");
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
app.UseNickErpTenancy();

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ---------------------------------------------------------------------------
// Image pipeline endpoint — streams the pre-rendered derivative for a given
// ScanArtifact. Auth required (analyst flow); ETag against the render row's
// content hash so the browser short-circuits to 304 on subsequent loads.
// NO Range support yet, NO byte-range, NO output caching attribute (would
// re-serialize the stream and corrupt the bytes per ARCHITECTURE §7.7).
// ---------------------------------------------------------------------------
app.MapGet("/api/images/{scanArtifactId:guid}/{kind}", async (
    Guid scanArtifactId,
    string kind,
    HttpContext http,
    NickERP.Inspection.Database.InspectionDbContext db,
    NickERP.Inspection.Imaging.IImageStore store,
    Microsoft.Extensions.Options.IOptions<NickERP.Inspection.Imaging.ImagingOptions> opts,
    CancellationToken ct) =>
{
    if (!NickERP.Inspection.Imaging.RenderKinds.IsKnown(kind))
        return Results.BadRequest($"Unknown kind '{kind}'. Expected 'thumbnail' or 'preview'.");

    var row = await db.ScanRenderArtifacts.AsNoTracking()
        .FirstOrDefaultAsync(r => r.ScanArtifactId == scanArtifactId && r.Kind == kind, ct);
    if (row is null)
        return Results.NotFound("Render not available yet — pre-render worker hasn't reached this artifact.");

    // ETag handling — content-addressed, so the hash IS the version.
    var etag = $"\"{(row.ContentHash.Length >= 16 ? row.ContentHash[..16] : row.ContentHash)}\"";
    var ifNoneMatch = http.Request.Headers["If-None-Match"].ToString();
    if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch.Contains(etag, StringComparison.Ordinal))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    var stream = store.OpenRenderRead(scanArtifactId, kind);
    if (stream is null)
        return Results.NotFound("Render row exists but storage blob is missing — most likely a stale row after manual cleanup.");

    var o = opts.Value;
    http.Response.Headers.ETag = etag;
    http.Response.Headers.CacheControl =
        $"public, max-age={o.HttpCacheMaxAgeSeconds}, s-maxage={o.HttpCacheSharedMaxAgeSeconds}, immutable";
    return Results.Stream(stream, contentType: row.MimeType, enableRangeProcessing: false);
})
.RequireAuthorization();

// ---------------------------------------------------------------------------
// Phase F5 — health endpoints. Both are anonymous so kubelets / load
// balancers don't have to carry credentials. /healthz/live is the cheap
// liveness probe (process is up); /healthz/ready runs every "ready" check
// and returns 503 with a JSON body listing the failing component(s).
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

// Sprint D4 — expose the auto-generated top-level Program type so the
// E2E test project (tests/NickERP.Inspection.E2E.Tests) can spin up the
// host via `WebApplicationFactory<Program>`. Without this, the generated
// Program is internal and the factory can't see it. No runtime cost.
public partial class Program;
