using Microsoft.AspNetCore.Authorization;
using NickERP.Platform.Demos.Identity.Components;
using NickERP.Platform.Identity;
using NickERP.Platform.Identity.Api;
using NickERP.Platform.Identity.Auth;
using NickERP.Platform.Identity.Database;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Identity (auth scheme + DB-backed resolver). Both calls are required —
// they mirror the production wiring documented in IDENTITY.md so the demo
// genuinely exercises the same surface real services consume.
// ---------------------------------------------------------------------------
builder.Services.AddNickErpIdentity(builder.Configuration, builder.Environment);
builder.Services.AddNickErpIdentityCore(
    builder.Configuration.GetConnectionString("Platform"));

// Default authorization policy: every endpoint or component that doesn't
// opt out requires a NickErp.Identity-authenticated principal.
builder.Services.AddAuthorization(opts =>
{
    opts.DefaultPolicy = new AuthorizationPolicyBuilder(CfAccessAuthenticationOptions.SchemeName)
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

// Blazor Server (interactive server-side rendering only — no WASM in the demo).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// ---------------------------------------------------------------------------
// Mount the Identity admin REST API on the same host. Every endpoint is
// gated by the Identity.Admin scope inside MapNickErpIdentityAdmin(). External
// clients (curl, the .http file, future module hosts) hit it directly.
// ---------------------------------------------------------------------------
app.MapNickErpIdentityAdmin();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
