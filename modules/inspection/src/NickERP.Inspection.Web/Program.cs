using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using NickERP.Inspection.Database;
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

// Inspection's own DbContext.
builder.Services.AddDbContext<InspectionDbContext>(opts =>
{
    opts.UseNpgsql(inspectionConn ?? throw new InvalidOperationException(
        "ConnectionStrings:Inspection is required (the nickerp_inspection Postgres DB)."),
        npgsql => npgsql.MigrationsAssembly(typeof(InspectionDbContext).Assembly.GetName().Name));
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

app.Run();
