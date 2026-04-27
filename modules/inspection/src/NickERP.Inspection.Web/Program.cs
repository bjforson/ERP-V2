using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Database;
using NickERP.Inspection.Imaging;
using NickERP.Inspection.Web.Components;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Identity;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Logging;
using NickERP.Platform.Plugins;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;

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

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

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

app.Run();
