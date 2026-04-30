using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Protocols.Configuration;

namespace NickFinance.WebApp.Services;

/// <summary>
/// Cloudflare Access ⇄ ASP.NET Core integration. Cloudflare Access fronts
/// every public hit on <c>finance.nickscan.net</c>; once the user clears
/// the email-OTP step, CF injects a signed JWT in the
/// <c>Cf-Access-Jwt-Assertion</c> header on every onward request.
///
/// This module decodes that JWT, validates the signature against CF's
/// rotating JWKS, and exposes the claims to the rest of the app via the
/// standard ASP.NET Core <c>HttpContext.User</c> + Blazor's
/// <c>AuthenticationStateProvider</c>.
///
/// Configuration (machine env vars or appsettings):
/// <list type="bullet">
///   <item><c>NickFinance:CfAccess:TeamDomain</c> — e.g. <c>nickscan.cloudflareaccess.com</c>.
///         If unset, auth is disabled and every request is treated as the
///         configured dev user (intended only for local <c>dotnet run</c>).</item>
///   <item><c>NickFinance:CfAccess:Audience</c> — the AUD tag of the
///         finance Access app, copied from the CF dashboard.
///         If unset, signature is validated but audience is not — ops
///         should set this before the app sees real customer data.</item>
/// </list>
/// </summary>
public static class CfAccessAuth
{
    public const string SchemeName = "CloudflareAccess";
    public const string HeaderName = "Cf-Access-Jwt-Assertion";

    /// <summary>
    /// Cookie auth scheme name. Used by the /login + /logout flow that
    /// authenticates against NickHR's password store (see PasswordVerifier).
    /// W4 (2026-04-29).
    /// </summary>
    public const string CookieSchemeName = "Cookie";

    /// <summary>
    /// Name of the persistent auth cookie. Picked the namespaced form so a
    /// reverse-proxy stack (CF + multiple sub-apps on *.nickscan.net) can
    /// distinguish whose cookie this is. Cookie scope is the host only, but
    /// belt-and-braces.
    /// </summary>
    public const string CookieName = "nickfinance.auth";

    /// <summary>
    /// Smart policy scheme name — exposed so the auth-policy registration
    /// (and tests) can name it without re-stringing.
    /// </summary>
    public const string SmartScheme = "Smart";

    /// <summary>
    /// Wire CF Access JWT validation into the auth pipeline. Returns
    /// <c>true</c> if auth was actually configured; <c>false</c> if the
    /// team domain isn't set (caller should fall back to dev-user mode).
    /// </summary>
    public static bool AddCloudflareAccess(this WebApplicationBuilder builder)
    {
        var teamDomain = builder.Configuration["NickFinance:CfAccess:TeamDomain"];
        var audience = builder.Configuration["NickFinance:CfAccess:Audience"];
        var isProduction = builder.Environment.IsProduction();

        // Fail-closed in Production: if either of the two CF Access knobs is
        // missing, refuse to start. The historical "no team domain → dev mode"
        // path remains for local dotnet-run scenarios but ONLY when the
        // hosting environment is non-Production. Without this gate, wiping
        // a single env var silently downgrades production to "everyone is
        // dev@nickscan.com" with no auth.
        if (isProduction)
        {
            if (string.IsNullOrWhiteSpace(teamDomain))
            {
                throw new InvalidOperationException(
                    "NickFinance:CfAccess:TeamDomain is required in Production. " +
                    "Set the machine env var NickFinance__CfAccess__TeamDomain to your " +
                    "<team>.cloudflareaccess.com hostname before restarting the service.");
            }
            if (string.IsNullOrWhiteSpace(audience))
            {
                throw new InvalidOperationException(
                    "NickFinance:CfAccess:Audience is required in Production. " +
                    "Set the machine env var NickFinance__CfAccess__Audience to the AUD tag " +
                    "of the finance Access app (visible in the CF dashboard). Without it, " +
                    "any JWT issued for any Access app on the same team domain would " +
                    "authenticate to NickFinance.");
            }
        }
        else if (string.IsNullOrWhiteSpace(teamDomain))
        {
            // Non-Production with no team domain → local dev. Skip auth registration
            // so dotnet-run-against-localhost doesn't 401 on every request.
            return false;
        }

        var issuer = $"https://{teamDomain}";
        var jwksUrl = $"{issuer}/cdn-cgi/access/certs";

        // "Smart" forwarding scheme — selects per-request which underlying
        // scheme should authenticate. Priority (W4, 2026-04-29):
        //   1. Persistent cookie present → Cookie scheme (user explicitly
        //      logged in via /login; cookie auth must beat both other
        //      paths so a CF-Access-fronted user who logged in still
        //      keeps their per-user identity).
        //   2. CF Access JWT header present → CloudflareAccess scheme
        //      (still authenticated by the edge — no cookie was minted).
        //   3. Otherwise → LAN-trust handler (returns NoResult if the
        //      source IP is outside the configured CIDR, which then 401s
        //      with a clean WWW-Authenticate challenge from CF Access).
        builder.Services
            .AddAuthentication(SmartScheme)
            .AddPolicyScheme(SmartScheme, "Cookie, CF Access, or LAN-trust", opts =>
            {
                opts.ForwardDefaultSelector = ctx =>
                {
                    if (ctx.Request.Cookies.ContainsKey(CookieName))
                    {
                        return CookieSchemeName;
                    }
                    if (ctx.Request.Headers.ContainsKey(HeaderName))
                    {
                        return SchemeName; // CF Access JWT present
                    }
                    // No cookie, no JWT → see if LAN-trust handler is
                    // interested. Handler self-gates on env vars + CIDR
                    // allowlist; returns NoResult if not configured.
                    return NickFinance.WebApp.Identity.LanTrustAuthHandler.SchemeName;
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, NickFinance.WebApp.Identity.LanTrustAuthHandler>(
                NickFinance.WebApp.Identity.LanTrustAuthHandler.SchemeName,
                _ => { })
            .AddCookie(CookieSchemeName, opts =>
            {
                // Persistent cookie for the email + password login path.
                // DataProtection keys (registered separately in Program.cs)
                // are how this cookie survives a service restart.
                opts.Cookie.Name = CookieName;
                opts.Cookie.HttpOnly = true;

                // SameAsRequest, NOT Always: the LAN-direct path is plain
                // HTTP (e.g. http://10.0.1.254:5500/) — Always would drop
                // the cookie outright on those connections, defeating the
                // whole point of "log in as yourself on LAN". CF-fronted
                // hits are HTTPS; the policy upgrades automatically there.
                // The trade-off: a LAN-segment attacker could sniff the
                // cookie. Mitigation: LAN-segment is already-trusted
                // territory by the LAN-trust scheme it exists alongside,
                // and the corporate LAN is firewalled from the public
                // internet.
                opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                opts.Cookie.SameSite = SameSiteMode.Lax;

                opts.LoginPath = "/login";
                opts.LogoutPath = "/logout";
                opts.AccessDeniedPath = "/access-not-provisioned";

                // Workday session. Sliding so an actively-used tab keeps
                // re-extending; an idle tab eventually expires and forces
                // a fresh password.
                opts.ExpireTimeSpan = TimeSpan.FromHours(8);
                opts.SlidingExpiration = true;

                // Keep claim names as we emit them. The SecurityAuditService
                // reads "nickerp.via" directly; mapping inbound claims would
                // rename it.
                opts.ClaimsIssuer = "NickFinance";
            })
            .AddJwtBearer(SchemeName, opts =>
            {
                // CF puts the JWT in their own header, not the standard
                // Authorization header — point JwtBearer at the right spot.
                opts.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        if (ctx.Request.Headers.TryGetValue(HeaderName, out var jwt) && jwt.Count > 0)
                        {
                            ctx.Token = jwt[0];
                        }
                        return Task.CompletedTask;
                    }
                };

                // Auto-discover JWKS from the team domain. Microsoft.Identity
                // refreshes it every 12h by default — fine for CF's key rotation.
                opts.MetadataAddress = $"{issuer}/.well-known/openid-configuration";
                opts.Authority = issuer;

                // ValidateAudience is now ALWAYS true in Production (the
                // throws above guarantee a non-empty audience reaches here).
                // For non-Production we still allow it to be skipped so a
                // local dev with no CF setup can run.
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = !string.IsNullOrWhiteSpace(audience),
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "email",
                };

                opts.RequireHttpsMetadata = true;
                opts.MapInboundClaims = false; // keep claims as CF emits them
            });

        // Default + fallback policy = "must be authenticated via Cookie, CF
        // Access, OR LAN-trust". All three schemes are listed so an
        // authenticated principal from any path satisfies the policy. The
        // fine-grained checks happen via [Authorize(Policy=...)] on each
        // page; see NickFinance.WebApp.Identity.PolicyRegistration for the
        // per-policy role-set wiring.
        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                    CookieSchemeName,
                    SchemeName,
                    NickFinance.WebApp.Identity.LanTrustAuthHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build())
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                    CookieSchemeName,
                    SchemeName,
                    NickFinance.WebApp.Identity.LanTrustAuthHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build());

        return true;
    }

    /// <summary>
    /// Map a validated CF Access ClaimsPrincipal to a <see cref="CurrentUser"/>.
    /// </summary>
    public static CurrentUser ToCurrentUser(ClaimsPrincipal user, long tenantId)
    {
        // CF Access JWTs carry the user's email under the "email" claim and
        // a stable Cloudflare user id under "sub" / "identity_nonce". We use
        // the email both as the display label AND as the seed for a
        // deterministic UserId — that way the same operator gets the same
        // GUID across logins, which is what the audit columns need.
        var email = user.FindFirst("email")?.Value
                 ?? user.FindFirst(ClaimTypes.Email)?.Value
                 ?? "unknown@nickscan.com";
        var sub = user.FindFirst("sub")?.Value;
        var name = user.FindFirst("name")?.Value
                ?? user.FindFirst(ClaimTypes.Name)?.Value
                ?? PrettyNameFromEmail(email);

        // Stable user id: SHA-256 of the email, namespaced. The hash is
        // deterministic, so re-logins always produce the same GUID.
        var userId = StableGuid("nickerp-cfaccess:" + (sub ?? email).ToLowerInvariant());

        return new CurrentUser(userId, name, email, tenantId);
    }

    private static string PrettyNameFromEmail(string email)
    {
        // "nicholas.kafiti@nickscan.com" → "Nicholas Kafiti"
        var local = email.Split('@', 2)[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return email;
        return string.Join(' ', parts.Select(p => p.Length switch
        {
            0 => p,
            1 => char.ToUpperInvariant(p[0]).ToString(),
            _ => char.ToUpperInvariant(p[0]) + p[1..]
        }));
    }

    private static Guid StableGuid(string seed)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(seed);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        var guidBytes = new byte[16];
        Array.Copy(hash, guidBytes, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }
}
