using Microsoft.AspNetCore.Authorization;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Identity;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Database;
using NickERP.Platform.Logging;
using NickERP.Platform.Telemetry;
using NickERP.Platform.Tenancy;
using NickERP.Platform.Tenancy.Database;
using NickERP.Portal.Components;

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

// Blazor Server (interactive server-side rendering).
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
app.UseNickErpTenancy(); // resolves nickerp:tenant_id claim into ITenantContext

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
