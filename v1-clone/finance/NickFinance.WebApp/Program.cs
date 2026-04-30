using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Observability;
using NickFinance.AP;
using NickFinance.AR;
using NickFinance.Banking;
using NickFinance.Budgeting;
using NickFinance.Coa;
using NickFinance.FixedAssets;
using NickERP.Platform.Identity;
using NickFinance.Itaps;
using NickFinance.Ledger;
// ITenantAccessor lives in NickERP.Platform.Identity now (extracted from
// NickFinance.Identity in Wave 3B / Track A.2 — the old assembly is a
// type-forwarding shim until every consumer migrates the using line).
using NickFinance.PettyCash;
using NickFinance.PettyCash.Approvals;
using NickFinance.PettyCash.Receipts;
using NickFinance.PettyCash.Recurring;
using NickFinance.Reporting;
using NickFinance.WebApp.Components;
using NickFinance.WebApp.Endpoints;
using NickFinance.WebApp.Identity;
using NickFinance.WebApp.Middleware;
using NickFinance.WebApp.Services;

var builder = WebApplication.CreateBuilder(args);
NickFinance.Pdf.QuestPdfBootstrap.Configure();
builder.AddNickErpLogging("NickFinance.WebApp");
builder.AddNickErpObservability("NickFinance.WebApp");

// When running as a Windows Service the SCM owns the lifecycle.
// UseWindowsService is a no-op when the host isn't a service, so it's
// safe in dev too.
builder.Host.UseWindowsService(opts => opts.ServiceName = "NickFinance_WebApp");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Connection string — required.
var connectionString = builder.Configuration.GetConnectionString("Finance")
    ?? throw new InvalidOperationException("ConnectionStrings:Finance is required.");

// Tenant scoping is wired up before the DbContexts so the contexts can
// pick the accessor up via DI in their constructor. The AddDbContext
// registrations go through the factory overload so we can pass the
// scoped ITenantAccessor explicitly to the multi-arg constructor —
// EF's default constructor selection picks the (DbContextOptions)
// overload otherwise, which would silently disable the filter.
builder.Services.AddScoped<ITenantAccessor, HttpContextTenantAccessor>();

// Per-DbContext factory: every command goes through a
// TenantSessionInterceptor that prepends `SET nickerp.current_tenant_id`,
// which Postgres RLS policies (scripts/apply-rls-policies.sql) read to
// scope rows. The interceptor is constructed per scope from the request's
// ITenantAccessor so each request sees its own tenant.
static void AddInterceptor(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder opts, IServiceProvider sp)
{
    var tenant = sp.GetRequiredService<ITenantAccessor>();
    opts.AddInterceptors(new TenantSessionInterceptor(tenant));
}

builder.Services.AddDbContext<LedgerDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<PettyCashDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<CoaDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<ArDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<ApDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<BankingDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<FixedAssetsDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<BudgetingDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });
builder.Services.AddDbContext<IdentityDbContext>((sp, opts) => { opts.UseNpgsql(connectionString); AddInterceptor(opts, sp); });

// Domain services
builder.Services.AddScoped<ILedgerWriter, LedgerWriter>();
builder.Services.AddScoped<ILedgerReader, LedgerReader>();
builder.Services.AddScoped<IPeriodService, PeriodService>();
builder.Services.AddScoped<ICoaService, CoaService>();
builder.Services.AddScoped<IFinancialReports, FinancialReports>();
builder.Services.AddScoped<IApprovalEngine, SingleStepApprovalEngine>();
builder.Services.AddScoped<IPettyCashService, PettyCashService>();
builder.Services.AddScoped<IArService, ArService>();
builder.Services.AddScoped<IDunningService, DunningService>();
builder.Services.AddScoped<ICustomerStatementService, CustomerStatementService>();
builder.Services.AddScoped<IApService, ApService>();
builder.Services.AddScoped<IWhtCertificateService, WhtCertificateService>();
builder.Services.AddSingleton<BankCsvParserRegistry>();
builder.Services.AddScoped<IBankingService, BankingService>();

// === Wave 2B Phase 1: FX rates (read path) ===
// FxRateService implements the kernel-side IFxConverter so reports can
// translate cross-currency Money without the kernel referencing Banking.
// The HTTP-backed BoG provider activates iff NICKFINANCE_BOG_API_URL is
// set; otherwise it returns an empty rate set and the operator falls
// back to the manual UI at /banking/fx-rates/new.
builder.Services.AddScoped<IFxConverter, FxRateService>();
builder.Services.AddHttpClient<BogRateProvider>(http => http.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IBogRateProvider>(sp =>
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NICKFINANCE_BOG_API_URL"))
        ? new EmptyBogRateProvider()
        : sp.GetRequiredService<BogRateProvider>());
builder.Services.AddScoped<IRateImporter, RateImporter>();
builder.Services.AddHostedService<FxRateImportHostedService>();
// === Wave 3A FX Phase 2: revaluation (write path) ===
builder.Services.AddScoped<IFxRevaluationService, FxRevaluationService>();

builder.Services.AddScoped<IFixedAssetService, FixedAssetService>();
builder.Services.AddScoped<IBudgetingService, BudgetingService>();
builder.Services.AddScoped<IItapsExporter, ItapsExporter>();
builder.Services.AddScoped<IRecurringVoucherRunner, RecurringVoucherRunner>();
builder.Services.AddScoped<IManualJournalAccountValidator, NickFinance.WebApp.Identity.CoaManualJournalAccountValidator>();
builder.Services.AddScoped<IJournalService, JournalService>();
builder.Services.AddScoped<NickFinance.PettyCash.CashCounts.ICashCountService, NickFinance.PettyCash.CashCounts.CashCountService>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

// PDF generators (QuestPDF) — pure / stateless, see NickFinance.Pdf/LICENSING.md
// for the QuestPDF Community-license decision.
builder.Services.AddSingleton<NickFinance.Pdf.IInvoicePdfGenerator, NickFinance.Pdf.InvoicePdfGenerator>();
builder.Services.AddSingleton<NickFinance.Pdf.IVoucherPdfGenerator, NickFinance.Pdf.VoucherPdfGenerator>();
builder.Services.AddSingleton<NickFinance.Pdf.IReceiptPdfGenerator, NickFinance.Pdf.ReceiptPdfGenerator>();
builder.Services.AddSingleton<NickFinance.Pdf.ICustomerStatementPdfGenerator, NickFinance.Pdf.CustomerStatementPdfGenerator>();
builder.Services.AddSingleton<NickFinance.Pdf.IWhtCertificatePdfGenerator, NickFinance.Pdf.WhtCertificatePdfGenerator>();

// Identity, role service, audit log. The role service is used by the
// custom authorization handler to gate every policy-protected endpoint.
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<ISecurityAuditService, SecurityAuditService>();
builder.Services.AddScoped<IPendingApprovalsCounter, PendingApprovalsCounter>();
// Phase 1 of the role-overhaul wave: SoD enforcement at grant time.
// IIdentityProvisioningService.GrantRoleAsync (in NickHR) calls
// ISodService.ValidateGrantAsync before inserting a user_role row.
builder.Services.AddScoped<ISodService, SodService>();

// UX support services — friendly exception translation, money/date/phone
// formatters. The translator is the only one with state (singletons in
// the static helpers); registering it as a singleton keeps the surface
// trivial.
builder.Services.AddSingleton<IExceptionTranslator, ExceptionTranslator>();

// Receipt storage rooted at the configured path. Wrapped with
// AES-256-GCM at-rest encryption when the NICKFINANCE_RECEIPT_DEK
// machine env var holds a base64-encoded 32-byte key (provisioned by
// scripts/install-nickfinance-service.ps1). Without the key we fall
// back to plaintext + a startup warning — keeps local dev / CI green
// while flagging mis-deploys loudly in production logs.
var receiptRoot = builder.Configuration.GetValue<string>("NickFinance:ReceiptStorageRoot")
    ?? Path.Combine(AppContext.BaseDirectory, "receipts");
builder.Services.AddSingleton<IReceiptStorage>(sp =>
{
    var inner = new LocalDiskReceiptStorage(receiptRoot);
    var key = Environment.GetEnvironmentVariable("NICKFINANCE_RECEIPT_DEK");
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("NickFinance.Receipts");
    if (string.IsNullOrWhiteSpace(key))
    {
        log.LogWarning("NICKFINANCE_RECEIPT_DEK is not set; receipts will be stored UNENCRYPTED at {Root}. "
            + "Set the env var to enable AES-256-GCM at-rest encryption.", receiptRoot);
        return inner;
    }
    try
    {
        var enc = new EncryptedReceiptStorage(inner, key);
        log.LogInformation("Receipt at-rest encryption enabled (AES-256-GCM, root={Root}).", receiptRoot);
        return (IReceiptStorage)enc;
    }
    catch (ArgumentException ex)
    {
        log.LogError(ex, "NICKFINANCE_RECEIPT_DEK is malformed; falling back to plaintext storage. Fix the env var to re-enable encryption.");
        return inner;
    }
});
builder.Services.AddScoped<IReceiptService, ReceiptService>();

// External integrations. Each interface registers a *routing* implementation
// that uses the real adapter when its credential env var holds a non-placeholder
// value, and the sandbox/no-op default otherwise. The end-state effect is:
// drop in a real key, restart the service, the live path activates — no code
// or DI change needed. See finance/DEFERRED.md for the activation checklist.

// --- D1: GRA e-VAT (Hubtel partner adapter ready) ---
var evatBaseUrl = builder.Configuration["NickFinance:Evat:BaseUrl"] ?? "https://evat.hubtel.com";
var evatKey = Environment.GetEnvironmentVariable("NICKFINANCE_EVAT_API_KEY");
builder.Services.AddHttpClient<NickFinance.AR.HubtelEvatProvider>(http =>
{
    http.BaseAddress = new Uri(evatBaseUrl);
    if (!string.IsNullOrWhiteSpace(evatKey))
    {
        http.DefaultRequestHeaders.Add("X-Api-Key", evatKey);
    }
    http.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<NickFinance.AR.StubEvatProvider>();
builder.Services.AddSingleton<NickFinance.AR.IEvatProvider>(sp =>
    new NickFinance.AR.RoutingEvatProvider(
        real: sp.GetRequiredService<NickFinance.AR.HubtelEvatProvider>(),
        stub: sp.GetRequiredService<NickFinance.AR.StubEvatProvider>(),
        configuredKey: evatKey));

// --- D3: receipt OCR (Azure Document Intelligence adapter ready) ---
var ocrEndpoint = builder.Configuration["NickFinance:Ocr:Endpoint"] ?? "https://placeholder.cognitiveservices.azure.com";
var ocrKey = Environment.GetEnvironmentVariable("AZURE_DOCUMENT_INTELLIGENCE_KEY");
builder.Services.AddHttpClient("AzureDocumentIntelligence", http => http.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<NickFinance.PettyCash.Receipts.NoopOcrEngine>();
builder.Services.AddSingleton<NickFinance.PettyCash.Receipts.AzureFormRecognizerOcrEngine>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("AzureDocumentIntelligence");
    return new NickFinance.PettyCash.Receipts.AzureFormRecognizerOcrEngine(http, ocrEndpoint, ocrKey ?? string.Empty);
});
builder.Services.AddSingleton<NickFinance.PettyCash.Receipts.IOcrEngine>(sp =>
    new NickFinance.PettyCash.Receipts.RoutingOcrEngine(
        real: sp.GetRequiredService<NickFinance.PettyCash.Receipts.AzureFormRecognizerOcrEngine>(),
        noop: sp.GetRequiredService<NickFinance.PettyCash.Receipts.NoopOcrEngine>(),
        configuredKey: ocrKey));

// --- D2: MoMo disbursement (NickComms.Gateway adapter ready) ---
var momoBaseUrl = builder.Configuration["NickFinance:Momo:GatewayBaseUrl"] ?? "http://localhost:5220";
var momoKey = Environment.GetEnvironmentVariable("NICKCOMMS_API_KEY_NICKFINANCE");
builder.Services.AddHttpClient<NickFinance.PettyCash.Disbursement.NickCommsMomoChannel>(http =>
{
    http.BaseAddress = new Uri(momoBaseUrl);
    if (!string.IsNullOrWhiteSpace(momoKey))
    {
        http.DefaultRequestHeaders.Add("X-Api-Key", momoKey);
    }
    http.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<NickFinance.PettyCash.Disbursement.OfflineCashChannel>();
builder.Services.AddSingleton<NickFinance.PettyCash.Disbursement.IDisbursementChannel>(sp =>
{
    // When the MoMo key isn't real, every disburse falls through to cash —
    // mirrors how the AR / OCR routes work.
    var keyIsReal = !string.IsNullOrWhiteSpace(momoKey)
        && !momoKey.StartsWith("PLACEHOLDER", StringComparison.OrdinalIgnoreCase);
    return keyIsReal
        ? sp.GetRequiredService<NickFinance.PettyCash.Disbursement.NickCommsMomoChannel>()
        : sp.GetRequiredService<NickFinance.PettyCash.Disbursement.OfflineCashChannel>();
});

// --- Email (SMTP) — env-var-gated; falls back to no-op when host or
//     username are missing. See finance/DEFERRED.md for the activation
//     checklist; pattern mirrors the other adapter routes above.
{
    var smtpOpts = NickFinance.WebApp.Services.EmailServiceFactory.ReadOptionsFromEnv();
    if (smtpOpts is not null)
    {
        builder.Services.AddSingleton(smtpOpts);
        builder.Services.AddSingleton<NickFinance.WebApp.Services.IEmailService, NickFinance.WebApp.Services.SmtpEmailService>();
    }
    else
    {
        builder.Services.AddSingleton<NickFinance.WebApp.Services.IEmailService, NickFinance.WebApp.Services.NoopEmailService>();
    }
}

// --- D5: WhatsApp approval notifications (Meta Cloud API adapter ready) ---
var waToken = Environment.GetEnvironmentVariable("WHATSAPP_CLOUD_API_TOKEN");
var waPhoneId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID");
var waTemplate = builder.Configuration["NickFinance:WhatsApp:TemplateName"];
var waLanguage = builder.Configuration["NickFinance:WhatsApp:LanguageCode"];
builder.Services.AddHttpClient("WhatsAppCloudApi", http =>
{
    http.BaseAddress = new Uri("https://graph.facebook.com");
    if (!string.IsNullOrWhiteSpace(waToken))
    {
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", waToken);
    }
    http.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddSingleton<NickFinance.PettyCash.Approvals.NoopApprovalNotifier>();
builder.Services.AddSingleton<NickFinance.PettyCash.Approvals.WhatsAppApprovalNotifier>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("WhatsAppCloudApi");
    return new NickFinance.PettyCash.Approvals.WhatsAppApprovalNotifier(http, waPhoneId ?? string.Empty, waTemplate, waLanguage);
});
builder.Services.AddSingleton<NickFinance.PettyCash.Approvals.IApprovalNotifier>(sp =>
    new NickFinance.PettyCash.Approvals.RoutingApprovalNotifier(
        real: sp.GetRequiredService<NickFinance.PettyCash.Approvals.WhatsAppApprovalNotifier>(),
        noop: sp.GetRequiredService<NickFinance.PettyCash.Approvals.NoopApprovalNotifier>(),
        configuredToken: waToken));
// Identity-backed phone resolver — replaces the no-op so the WhatsApp
// outbound + inbound paths actually find users by phone. Scoped because
// it depends on the scoped IdentityDbContext.
builder.Services.AddScoped<NickFinance.PettyCash.Approvals.IApproverPhoneResolver, NickFinance.WebApp.Identity.IdentityApproverPhoneResolver>();

// Cloudflare Access auth — production only. When TeamDomain is set in
// configuration the JwtBearer middleware validates Cf-Access-Jwt-Assertion
// against CF's JWKS and populates HttpContext.User. When unset (local
// dev), auth is skipped and CurrentUser falls back to the configured
// DevUser block — mirrors how dotnet run worked before this wiring landed.
var cfAccessOn = builder.AddCloudflareAccess();
builder.Services.AddHttpContextAccessor();

// W4 (2026-04-29) — Cookie auth + DataProtection.
//
//   • PasswordVerifier reads NickHR's public."Users" via a fresh Npgsql
//     connection per verify (deliberately bypassing the EF tenant
//     interceptor — the SELECT runs against schema 'public' which has
//     no RLS policies attached).
//   • DataProtection keys persist to disk so the auth cookie survives
//     a service restart. Without this, every restart of NickFinance_WebApp
//     invalidates every cookie and forces every user to re-login.
//   • The directory MUST be readable + writable by the service's virtual
//     account 'NT SERVICE\NickFinance_WebApp'. The deploy script
//     pre-creates it; if missing, ASP.NET Core falls back to in-memory
//     keys with a startup warning (which means cookies still work but
//     don't survive restarts).
builder.Services.AddSingleton<NickFinance.WebApp.Identity.IPasswordVerifier>(sp =>
    new NickFinance.WebApp.Identity.PasswordVerifier(
        connectionString,
        sp.GetRequiredService<ILogger<NickFinance.WebApp.Identity.PasswordVerifier>>()));

{
    var dpKeyDir = Environment.GetEnvironmentVariable("NICKFINANCE_DATAPROTECTION_KEYS")
        ?? @"C:\Shared\NSCIM_PRODUCTION\Data\NickFinance.WebApp\dp-keys";
    try
    {
        Directory.CreateDirectory(dpKeyDir);
    }
    catch
    {
        // Best-effort. If we can't create it, AddDataProtection's own
        // fallback to in-memory keys kicks in and logs a warning.
    }
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dpKeyDir))
        .SetApplicationName("NickFinance.WebApp");
}

// NickFinance authorization — claim/permission-based (NSCIM pattern).
//
// Phase 2 of the role overhaul (2026-04-30) replaced the role-list
// policies (Policies.SubmitVoucher → [SiteCashier, SiteCustodian, ...])
// with permission-claim-resolution: every Razor page uses
// [Authorize(Policy = Permissions.X)]; the DynamicAuthorizationPolicyProvider
// resolves any policy name with a dot ("petty.voucher.approve") into a
// one-shot PermissionRequirement; the PermissionAuthorizationHandler
// looks up the user's bundle via IPermissionService (backed by
// identity.role_permissions). The role-list AddNickFinanceAuthorization
// extension + RoleAuthorizationHandler are gone.
builder.Services.AddAuthorization();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider,
    NickFinance.WebApp.Identity.DynamicAuthorizationPolicyProvider>();
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    NickFinance.WebApp.Identity.PermissionAuthorizationHandler>();
builder.Services.AddScoped<NickFinance.WebApp.Identity.IPermissionService,
    NickFinance.WebApp.Identity.PermissionService>();

// Current user — persistent lookup against identity.users now that the
// schema exists. W3B (2026-04-29): Production CF-Access path is now
// lookup-only — if HR hasn't provisioned the user, the factory throws
// AccessNotProvisionedException and the middleware below short-circuits
// to /access-not-provisioned. Dev/local `dotnet run` keeps lazy-create.
builder.Services.AddScoped(sp =>
{
    var tenant = builder.Configuration.GetValue<long?>("NickFinance:DefaultTenantId") ?? 1L;
    return PersistentCurrentUserFactory.Resolve(sp, builder.Configuration, cfAccessOn, tenant, builder.Environment);
});

// Blazor cascade so AuthorizeView / AuthenticationStateProvider work.
if (cfAccessOn)
{
    builder.Services.AddCascadingAuthenticationState();
    // Re-validate the authenticated principal every 30 minutes for the
    // life of the circuit. Without this, a CF Access revocation only
    // bites once the user reloads.
    builder.Services.AddScoped<AuthenticationStateProvider, CfAccessRevalidatingAuthStateProvider>();
}

// Rate limiting — per-user throttle for the app, per-IP for the
// anonymous WhatsApp webhook, /metrics is exempt (loopback gates it
// already).
builder.Services.AddRateLimiter(opts =>
{
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // W4 (2026-04-29) — named "login" policy applied to /login/submit
    // by AuthEndpoints. 5 attempts per IP per minute is enough for a
    // typo-then-retry but slow enough that a credential-stuffing run
    // hits the wall fast. 429 → the login UI shows "rate-limited" via
    // the redirect-on-error pattern (the policy's RejectionStatusCode
    // overrides what the route returns; the user just sees the 429
    // page if they push past).
    opts.AddPolicy(NickFinance.WebApp.Endpoints.AuthEndpoints.LoginRateLimitPolicy,
        http => RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "login:" + (http.Connection.RemoteIpAddress?.ToString() ?? "anon"),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    // Default per-authenticated-user limiter (used by everything
    // EXCEPT the explicitly-named partitions below).
    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var path = http.Request.Path.Value ?? string.Empty;
        // Exempt loopback /metrics and the WhatsApp webhook (own partition).
        if (path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("metrics");
        }
        if (path.StartsWith("/api/whatsapp/webhook", StringComparison.OrdinalIgnoreCase))
        {
            // Per-IP, 20 req/min.
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter("waw:" + ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }
        // Per-authenticated-user (or per-IP if anonymous) at 120 req/min.
        var key = http.User.Identity?.IsAuthenticated == true
            ? "u:" + (http.User.FindFirst("sub")?.Value ?? http.User.Identity?.Name ?? "anon")
            : "ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "anon");
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Security headers are part of every response — public assets, error
// pages, and SignalR alike. Sits as early as possible so even fast-paths
// (e.g. anti-forgery 400s) carry the locked-down CSP.
app.UseSecurityHeaders();

app.UseStaticFiles();
app.UseAntiforgery();

// Correlation + Prometheus must run BEFORE the auth pipeline so /metrics
// (which registers as middleware via UseWhen, not as a routed endpoint)
// isn't 401'd by the CF Access fallback policy. The Prometheus middleware
// has its own loopback/CIDR gate — that's the security boundary here.
app.UseNickErpCorrelation();
app.MapNickErpMetrics();

if (cfAccessOn)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// W3B (2026-04-29): when the provisioned-only factory throws
// AccessNotProvisionedException, redirect the request to the friendly
// "ask HR to provision your access" page instead of bubbling a 500.
// We sit AFTER auth so the principal is populated, but BEFORE the
// rate-limiter and Blazor endpoints — anything later that resolves
// CurrentUser will throw and we'll catch it here.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (NickFinance.WebApp.Identity.AccessNotProvisionedException ex)
    {
        // Don't redirect if the user is already on the page (avoid loops).
        if (ctx.Response.HasStarted ||
            ctx.Request.Path.StartsWithSegments("/access-not-provisioned", StringComparison.OrdinalIgnoreCase))
        {
            throw;
        }
        var qs = $"?email={Uri.EscapeDataString(ex.Email)}"
               + (string.IsNullOrEmpty(ex.CfAccessSub) ? "" : $"&sub={Uri.EscapeDataString(ex.CfAccessSub)}");
        ctx.Response.Redirect("/access-not-provisioned" + qs);
    }
});

// Rate limiting runs AFTER auth so per-user partitions can read the
// authenticated identity. The /metrics path is exempt at the partitioner
// level — see GlobalLimiter setup above.
app.UseRateLimiter();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// === Wave 1C: PDF endpoints (invoice / voucher / receipt) ===
app.MapPdfEndpoints();

// === W4 (2026-04-29): /login/submit + /logout endpoints ===
// The Login.razor page POSTs the form here because Blazor Server's
// interactive renderer can't write the auth cookie (no HttpResponse
// over a SignalR circuit).
app.MapAuthEndpoints();

// === D5: WhatsApp inbound webhook ===
// Meta WhatsApp Cloud API webhook callback. The endpoint is anonymous
// (Meta cannot present a CF Access JWT); HMAC-SHA256 over the raw body
// using WHATSAPP_WEBHOOK_SECRET is the auth boundary.
{
    var verifyToken = Environment.GetEnvironmentVariable("WHATSAPP_WEBHOOK_VERIFY_TOKEN");
    var webhookSecret = Environment.GetEnvironmentVariable("WHATSAPP_WEBHOOK_SECRET");

    // GET — Meta verification handshake. AllowAnonymous because Meta's
    // edge cannot present a CF Access JWT; HMAC + verify-token below
    // are the auth boundary.
    var get = app.MapGet("/api/whatsapp/webhook", (HttpContext ctx) =>
    {
        var mode = ctx.Request.Query["hub.mode"].ToString();
        var token = ctx.Request.Query["hub.verify_token"].ToString();
        var challenge = ctx.Request.Query["hub.challenge"].ToString();
        if (string.IsNullOrEmpty(verifyToken)) return Results.StatusCode(503);
        if (mode == "subscribe" && token == verifyToken)
            return Results.Text(challenge, "text/plain");
        return Results.Unauthorized();
    }).AllowAnonymous();

    // POST — message delivery (HMAC-SHA256 over raw body is the auth boundary)
    var post = app.MapPost("/api/whatsapp/webhook", async (
        HttpContext ctx,
        NickFinance.PettyCash.IPettyCashService pcSvc,
        NickFinance.PettyCash.Approvals.IApproverPhoneResolver phoneResolver,
        NickFinance.PettyCash.PettyCashDbContext db,
        ILogger<Program> log) =>
    {
        if (string.IsNullOrEmpty(webhookSecret))
        {
            log.LogWarning("WHATSAPP_WEBHOOK_SECRET not set; rejecting all webhooks until configured.");
            return Results.StatusCode(503);
        }

        ctx.Request.EnableBuffering();
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        var raw = ms.ToArray();
        ctx.Request.Body.Position = 0;

        var sig = ctx.Request.Headers["X-Hub-Signature-256"].ToString();
        const string prefix = "sha256=";
        if (!sig.StartsWith(prefix)) return Results.Unauthorized();
        var providedHex = sig[prefix.Length..];
        using var hmac = new System.Security.Cryptography.HMACSHA256(System.Text.Encoding.UTF8.GetBytes(webhookSecret));
        var computed = Convert.ToHexString(hmac.ComputeHash(raw)).ToLowerInvariant();
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(providedHex.ToLowerInvariant()),
                System.Text.Encoding.ASCII.GetBytes(computed)))
        {
            return Results.Unauthorized();
        }

        // Parse Meta payload — minimal extraction
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("entry", out var entries) || entries.GetArrayLength() == 0) return Results.Ok(new { received = true });
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes)) continue;
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value)) continue;
                    if (!value.TryGetProperty("messages", out var messages)) continue;
                    foreach (var msg in messages.EnumerateArray())
                    {
                        var from = msg.GetProperty("from").GetString() ?? "";
                        var body = msg.TryGetProperty("text", out var text) && text.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
                        var match = System.Text.RegularExpressions.Regex.Match(body, @"^APPROVE\s+(PC-[A-Z0-9-]+)(?:\s+(.+))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!match.Success) continue;
                        var voucherNo = match.Groups[1].Value;
                        var commentReply = match.Groups.Count > 2 ? match.Groups[2].Value : null;

                        var phoneE164 = from.StartsWith("+") ? from : "+" + from;
                        var approverId = await phoneResolver.ResolveUserIdByPhoneAsync(phoneE164);
                        if (approverId is null)
                        {
                            log.LogWarning("WhatsApp APPROVE from unknown phone {Phone}; ignoring.", phoneE164);
                            continue;
                        }

                        var voucher = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(db.Vouchers, v => v.VoucherNo == voucherNo);
                        if (voucher is null)
                        {
                            log.LogWarning("WhatsApp APPROVE for unknown voucher {VoucherNo}; ignoring.", voucherNo);
                            continue;
                        }

                        await pcSvc.ApproveVoucherAsync(voucher.VoucherId, approverId.Value, amountApprovedMinor: null, comment: $"WhatsApp reply: {commentReply ?? "(no comment)"}");
                        log.LogInformation("WhatsApp APPROVE landed for voucher {VoucherNo} by {Approver}", voucherNo, approverId.Value);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "WhatsApp webhook payload parse failure");
        }
        return Results.Ok(new { received = true });
    }).AllowAnonymous();
}

app.Run();
