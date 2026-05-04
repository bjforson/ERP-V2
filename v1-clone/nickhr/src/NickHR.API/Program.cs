using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using NickERP.Platform.Tenancy;
using NickHR.API.Security;
using NickHR.Infrastructure;
using NickHR.Infrastructure.Data;
using NickHR.Services;
using Serilog;

// Single instance enforcement
using var mutex = new Mutex(true, @"Global\NickHR_API_SingleInstance", out var createdNew);
if (!createdNew)
{
    Log.Warning("NickHR API is already running. Exiting.");
    return;
}

// Enable legacy timestamp behavior for Npgsql
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Windows Service support
builder.Host.UseWindowsService();

// Resolve DB password from environment variable.
// API connection string uses Username=nscim_app (migrated in commit a5067a2),
// so it reads NICKSCAN_DB_PASSWORD which holds the nscim_app role's password.
// (The WebApp project still uses Username=postgres and reads NICKHR_DB_PASSWORD;
// they intentionally diverge until the WebApp is also migrated.)
// 2026-04-27: fail-fast on missing NICKSCAN_DB_PASSWORD in production.
// Pre-fix behaviour was a silent fallback to the literal "postgres" — leftover from
// dev convenience — which made misconfiguration look like a 401-loop instead of a
// startup failure. Mirrors the JWT-key fail-fast pattern from this same file.
var dbPassword = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
if (string.IsNullOrEmpty(dbPassword))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "NICKSCAN_DB_PASSWORD environment variable is not set. NickHR.API cannot start in " +
            "production without the nscim_app role password. Set it via " +
            "[Environment]::SetEnvironmentVariable('NICKSCAN_DB_PASSWORD', <value>, 'Machine')");
    }
    Log.Warning("NICKSCAN_DB_PASSWORD not set — falling back to dev default 'postgres'. Production startup would fail here.");
    dbPassword = "postgres";
}

var connString = builder.Configuration.GetConnectionString("DefaultConnection");
if (!string.IsNullOrEmpty(connString) && connString.Contains("***USE_ENV_VAR_NICKSCAN_DB_PASSWORD***"))
{
    builder.Configuration["ConnectionStrings:DefaultConnection"] =
        connString.Replace("***USE_ENV_VAR_NICKSCAN_DB_PASSWORD***", dbPassword);
}

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("Logs/nickhr-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Infrastructure (DbContext, Identity, Repositories)
builder.Services.AddInfrastructure(builder.Configuration);

// Application Services
builder.Services.AddApplicationServices(builder.Configuration);

// JWT Authentication
// SECURITY: JWT signing key MUST come from the NICKHR_JWT_KEY environment variable
// in production. Leaving it in appsettings.json is the "secrets-in-git" anti-pattern.
// If the env var is missing OR the appsettings placeholder is still in place, fail
// fast at startup rather than silently falling back to a committed-in-history key.
var jwtKey = Environment.GetEnvironmentVariable("NICKHR_JWT_KEY")
             ?? builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Contains("***USE_ENV_VAR"))
{
    throw new InvalidOperationException(
        "JWT signing key not configured. Set the NICKHR_JWT_KEY environment variable " +
        "to a strong random value (>= 32 chars). Example (PowerShell, machine-scoped): " +
        "[Environment]::SetEnvironmentVariable('NICKHR_JWT_KEY', <value>, 'Machine')");
}
if (jwtKey.Length < 32)
{
    throw new InvalidOperationException(
        $"JWT signing key is {jwtKey.Length} chars; HS256 requires >= 32. Regenerate NICKHR_JWT_KEY.");
}

// Write the resolved key back into configuration so AuthService.GenerateJwtToken
// (which reads _configuration["Jwt:Key"]) signs with the SAME secret the
// JwtBearer middleware validates against. Without this overwrite, AuthService
// keeps reading the appsettings.json placeholder string ("***USE_ENV_VAR..."),
// signs every token with that literal, and the validator below — using the
// real env-var key — rejects every signature with IDX10517.
builder.Configuration["Jwt:Key"] = jwtKey;

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        // 60s skew tolerates routine clock drift across servers without granting
        // a meaningful extra-validity window. ZERO was rejecting valid tokens
        // when the API host and a client diverged by >1s.
        ClockSkew = TimeSpan.FromSeconds(60)
    };

    // ✅ SECURITY: Single-session enforcement (2026-04-25). Reject tokens
    // whose sid claim no longer matches ApplicationUser.CurrentSessionId.
    // The login flow rotates that column on every successful Login/Register,
    // so a fresh login on Device B invalidates Device A's previously-issued
    // token on its next request. See NickHR.API/Security/SingleSessionValidator.
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async ctx =>
        {
            var ok = await SingleSessionValidator.ValidateAsync(ctx.HttpContext, ctx.Principal);
            if (!ok)
            {
                ctx.Fail("Session invalidated by a newer login.");
            }
        },
        // Keep this — it surfaces signature mismatches (IDX10517), expired tokens
        // and other JWT failures into the Serilog stream so they're not silent.
        OnAuthenticationFailed = ctx =>
        {
            Serilog.Log.Warning(ctx.Exception, "JWT auth failed");
            return Task.CompletedTask;
        }
    };
});

// IMemoryCache backs the single-session validator's 30s sid lookup.
// SizeLimit caps total entry-count cost (each entry sets Size = 1) so a
// pathological burst can't OOM the API host. SingleSessionValidator already
// sets Size = 1 on its entries; any future cache.Set callers MUST also set
// Size or MemoryCache will throw.
builder.Services.AddMemoryCache(opts =>
{
    opts.SizeLimit = 100 * 1024 * 1024; // 100 MB cost units
});

// Health checks. /api/health runs everything; /api/health/live is a pure liveness
// probe (just confirms the process is up); /api/health/ready runs only checks
// tagged "ready" — currently the NickHR Postgres connection. NSCIM_API's downstream
// liveness probe was previously hitting /api/_module/manifest as a proxy because
// no /health endpoint existed; this replaces that hack.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<NickHRDbContext>(
        "NickHR_Database",
        tags: new[] { "database", "ready" });

// ✅ SECURITY: Deny-by-default. Every endpoint requires an authenticated user unless it
// explicitly opts in with [AllowAnonymous] (login, register, health). Closes the class
// of bugs where a controller forgot [Authorize] — see BiometricController history.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NickHR API",
        Version = "v1",
        Description = "NickHR Human Resource Management System API"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    // Swashbuckle 10.x + Microsoft.OpenApi 2.x: use OpenApiSecuritySchemeReference
    c.AddSecurityRequirement((document) => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", document),
            new List<string>()
        }
    });
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("NickHRCors", policy =>
    {
        policy.WithOrigins(
                builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? new[] { "https://localhost:5300" })
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

// SignalR
builder.Services.AddSignalR();

// ✅ SECURITY: Rate limiting on auth endpoints — login, refresh-token, register
// were previously unbounded. Token bucket: 10 attempts / minute / IP for auth,
// 100 / min for general requests. The "auth" policy applies to anything decorated
// with [EnableRateLimiting("auth")]; the global limiter catches everything else.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(
        httpContext => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = 200,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

// Seed data
await SeedData.InitializeAsync(app.Services);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();
app.UseCors("NickHRCors");
// UseRouting must run before UseRateLimiter for [EnableRateLimiting("auth")] on
// individual actions to be discovered by the rate-limiter middleware. Without
// explicit UseRouting() the implicit one runs after UseRateLimiter and the
// per-endpoint policies silently no-op.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// NICKSCAN ERP — resolves the tenant from the JWT tenant_id claim and feeds
// it into ITenantContext, which the TenantConnectionInterceptor then pushes
// down to Postgres as `app.tenant_id` so the tenant_isolation_* RLS policies
// can actually filter. Must run AFTER UseAuthentication so the JWT principal
// is available, and BEFORE controllers that touch tenant-owned data.
app.UseNickERPTenancy();

// Anonymous health endpoints. The deny-by-default fallback policy above (RequireAuthenticatedUser)
// rejects unauthenticated requests on every other endpoint; these explicitly opt out so probes
// (load balancer, NSCIM_API's /health downstream check) work without a JWT.
app.MapHealthChecks("/api/health").AllowAnonymous();
app.MapHealthChecks("/api/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false  // liveness: no checks, just process is up
}).AllowAnonymous();
app.MapHealthChecks("/api/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();

app.MapControllers();

app.Run();
