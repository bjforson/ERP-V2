using System.Text;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MudBlazor.Services;
using NickERP.Platform.Tenancy;
using NickHR.Infrastructure;
using NickHR.Infrastructure.Data;
using NickHR.Services;
using NickHR.WebApp.Components;
using NickHR.WebApp.Services;

// Single instance enforcement
using var mutex = new Mutex(true, @"Global\NickHR_WebApp_SingleInstance", out var createdNew);
if (!createdNew)
{
    // WebApp doesn't reference Serilog (unlike NickHR.API); Console.Error is
    // captured by the Windows Service stderr stream so the warning still lands
    // in the service event log instead of vanishing.
    Console.Error.WriteLine("[WARN] NickHR WebApp is already running. Exiting.");
    return;
}

// Enable legacy timestamp behavior for Npgsql
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Windows Service support
builder.Host.UseWindowsService();

// Resolve DB password from environment variable.
// Prefer NICKHR_DB_PASSWORD (the postgres-user password used by the nickhr DB),
// then fall back to NICKSCAN_DB_PASSWORD for backward compatibility.
// NOTE: NICKSCAN_DB_PASSWORD was repurposed in commit a5067a2 to hold the
// nscim_app role's password. NickHR still connects as postgres super-user, so
// it needs its own variable. Long-term fix: migrate NickHR to a dedicated
// nickhr_app role.
// 2026-04-27: fail-fast on missing DB password in production. Was silently
// falling through to "postgres", which only failed at first query.
var dbPassword = Environment.GetEnvironmentVariable("NICKHR_DB_PASSWORD")
                 ?? Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
if (string.IsNullOrEmpty(dbPassword))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "Neither NICKHR_DB_PASSWORD nor NICKSCAN_DB_PASSWORD environment variable is set. " +
            "NickHR.WebApp cannot start in production without the postgres-user password.");
    }
    dbPassword = "postgres";
}

var connString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connString) && connString.Contains("***USE_ENV_VAR_NICKSCAN_DB_PASSWORD***"))
{
    builder.Configuration["ConnectionStrings:DefaultConnection"] =
        connString.Replace("***USE_ENV_VAR_NICKSCAN_DB_PASSWORD***", dbPassword);
}

// Resolve SMTP password from environment variable.
// 2026-04-27: SMTP missing in production is now a startup failure rather than a
// silent empty-string substitution that surfaces as opaque 535 errors at send time.
var smtpPassword = Environment.GetEnvironmentVariable("NICKHR_SMTP_PASSWORD");
var smtpPwConfig = builder.Configuration["Email:Password"];
var smtpRequiresEnv = !string.IsNullOrEmpty(smtpPwConfig) && smtpPwConfig.Contains("***USE_ENV_VAR_");
if (smtpRequiresEnv)
{
    if (string.IsNullOrEmpty(smtpPassword) && builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "NICKHR_SMTP_PASSWORD environment variable is not set but Email:Password expects it. " +
            "Set the env var or remove the placeholder from appsettings.json.");
    }
    builder.Configuration["Email:Password"] = smtpPassword ?? string.Empty;
}

// Resolve Kestrel HTTPS certificate password from environment variable.
// Same fail-fast pattern as JWT/DB/SMTP: if appsettings still carries the
// "***USE_ENV_VAR_NICKHR_CERT_PASSWORD***" placeholder, require the env var
// in production. Set machine-wide via:
//   [Environment]::SetEnvironmentVariable('NICKHR_CERT_PASSWORD', <value>, 'Machine')
var certPassword = Environment.GetEnvironmentVariable("NICKHR_CERT_PASSWORD");
var certPwConfig = builder.Configuration["Kestrel:Endpoints:Https:Certificate:Password"];
var certRequiresEnv = !string.IsNullOrEmpty(certPwConfig) && certPwConfig.Contains("***USE_ENV_VAR_");
if (certRequiresEnv)
{
    if (string.IsNullOrEmpty(certPassword))
    {
        if (builder.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                "NICKHR_CERT_PASSWORD environment variable is not set but Kestrel HTTPS " +
                "certificate config expects it. Set it via " +
                "[Environment]::SetEnvironmentVariable('NICKHR_CERT_PASSWORD', <value>, 'Machine').");
        }
        Console.Error.WriteLine("[WARN] NICKHR_CERT_PASSWORD not set — HTTPS endpoint may fail to bind. Production startup would fail here.");
    }
    builder.Configuration["Kestrel:Endpoints:Https:Certificate:Password"] = certPassword ?? string.Empty;
}

// Add MudBlazor
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = false;
    config.SnackbarConfiguration.NewestOnTop = true;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
});

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Infrastructure (DbContext, Identity, Repositories)
builder.Services.AddInfrastructure(builder.Configuration);

// DbContextFactory for Blazor Server scoped components
// NICKSCAN ERP — Phase 1 multi-tenancy: factory uses (sp, options) overload so we
// can resolve the TenantOwnedEntityInterceptor from DI.
builder.Services.AddDbContextFactory<NickHR.Infrastructure.Data.NickHRDbContext>((sp, options) =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsql => npgsql.MigrationsAssembly(typeof(NickHR.Infrastructure.Data.NickHRDbContext).Assembly.FullName));
    options.AddInterceptors(sp.GetRequiredService<TenantOwnedEntityInterceptor>());
}, ServiceLifetime.Scoped);

// Data Protection - ephemeral keys (invalidates all cookies on service restart)
builder.Services.AddDataProtection()
    .UseEphemeralDataProtectionProvider();

// Configure Identity cookie - 15 minute idle timeout
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/login";
    options.AccessDeniedPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(15);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Name = "NickHR.Auth";
});

// Application Services
builder.Services.AddApplicationServices(builder.Configuration);

// Blazored LocalStorage
builder.Services.AddBlazoredLocalStorage();

// HTTP Client for API calls
builder.Services.AddHttpClient("NickHR.API", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:5206");
});
builder.Services.AddScoped<ApiClient>();

// Auth state - use cookie-based server auth (persists across refreshes)
builder.Services.AddScoped<AuthStateProvider>(); // Keep for backward compat
builder.Services.AddScoped<CurrentEmployeeService>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

// W3B (2026-04-29): NickHR is now system-of-record for users that consume
// any NickERP app (NickFinance today; NickScan tomorrow). The shared
// identity schema lives in the same Postgres `nickhr` database, so we
// reuse the existing connection. NickHR is a CONSUMER of the schema —
// the migration owner is NickFinance.Database.Bootstrap. We don't run
// EF migrations from this app.
builder.Services.AddDbContext<NickERP.Platform.Identity.IdentityDbContext>((sp, opts) =>
{
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<NickHR.WebApp.Services.IIdentityProvisioningService,
                          NickHR.WebApp.Services.IdentityProvisioningService>();

// W3B Phase 2 (2026-04-29): NickFinance access section needs an ISodService
// to surface segregation-of-duties warnings inline. Phase 1 (parallel) is
// shipping a real DB-backed implementation; until that lands we register
// the no-op fallback so the form never crashes on a missing service. When
// Phase 1's registration is added to this file it should REPLACE this line
// (TryAddScoped would silently keep the null impl, which is wrong).
builder.Services.AddScoped<NickHR.WebApp.Identity.ISodService,
                          NickHR.WebApp.Identity.NullSodService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// HTTPS redirect disabled - Cloudflare Tunnel handles SSL externally
// Direct HTTPS available on port 5311 for LAN access
app.UseStaticFiles();

// Serve uploaded files (photos, documents) from the shared uploads directory.
// Configurable via NickHR:UploadsPath; default keeps existing prod path.
var uploadsPath = builder.Configuration["NickHR:UploadsPath"]
    ?? @"C:\Shared\NSCIM_PRODUCTION\NickHR\uploads";
if (Directory.Exists(uploadsPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
        RequestPath = "/uploads"
    });
}

app.UseAuthentication();
app.UseAuthorization();
// NICKSCAN ERP — Phase 1 multi-tenancy resolution from claims (cookies for now,
// JWT for SSO in Phase 2). Falls back to default tenant 1.
app.UseNickERPTenancy();
app.UseAntiforgery();

// Cookie-based login callback - signs user in with Identity cookie
app.MapGet("/login-callback", async (string email,
    Microsoft.AspNetCore.Identity.SignInManager<ApplicationUser> signInManager,
    Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
    HttpContext httpContext) =>
{
    var user = await userManager.FindByEmailAsync(email);
    if (user != null)
    {
        await signInManager.SignInAsync(user, isPersistent: true);
    }
    httpContext.Response.Redirect("/");
}).AllowAnonymous();

// Cookie-based logout
app.MapGet("/logout", async (
    Microsoft.AspNetCore.Identity.SignInManager<ApplicationUser> signInManager,
    HttpContext httpContext) =>
{
    await signInManager.SignOutAsync();
    httpContext.Response.Redirect("/login");
}).AllowAnonymous();

// Serve employee photos from database (no auth required for images)
app.MapGet("/employee-photo/{id:int}", async (int id, IServiceProvider sp) =>
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NickHR.Infrastructure.Data.NickHRDbContext>();
    var emp = await db.Employees
        .Where(e => e.Id == id && !e.IsDeleted && e.PhotoData != null)
        .Select(e => new { e.PhotoData, e.PhotoContentType })
        .FirstOrDefaultAsync();
    if (emp?.PhotoData == null) return Results.NotFound();
    return Results.File(emp.PhotoData, emp.PhotoContentType ?? "image/jpeg");
}).AllowAnonymous();

// Serve documents from database
app.MapGet("/employee-document/{id:int}", async (int id, IServiceProvider sp) =>
{
    using var scope = sp.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NickHR.Infrastructure.Data.NickHRDbContext>();
    var doc = await db.EmployeeDocuments
        .Where(d => d.Id == id && !d.IsDeleted && d.FileData != null)
        .Select(d => new { d.FileData, d.ContentType, d.FileName })
        .FirstOrDefaultAsync();
    if (doc?.FileData == null) return Results.NotFound();
    return Results.File(doc.FileData, doc.ContentType ?? "application/octet-stream", doc.FileName);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
