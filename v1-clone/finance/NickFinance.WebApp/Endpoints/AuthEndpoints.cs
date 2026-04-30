using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using NickFinance.WebApp.Identity;
using NickFinance.WebApp.Services;

namespace NickFinance.WebApp.Endpoints;

/// <summary>
/// W4 (2026-04-29) — minimal API endpoints backing the email + password
/// login page (<see cref="NickFinance.WebApp.Components.Pages.Login"/>).
/// Lives outside the Blazor component tree because Blazor Server's
/// interactive renderer runs over a SignalR circuit — it has no
/// HttpResponse to write the auth cookie to. The Login page POSTs to
/// <c>/login/submit</c>; we verify the password against NickHR's store,
/// look up the matching <c>identity.users</c> row, then SignInAsync on
/// the cookie scheme.
///
/// <para>Audit-log details:</para>
/// <list type="bullet">
///   <item><description>Successful sign-in records
///   <c>SecurityAuditAction.Login</c> with result=Allowed and
///   details.via="cookie".</description></item>
///   <item><description>Failed sign-in records the same action with
///   result=Denied and the email tried (for forensic tracing — never
///   the password).</description></item>
///   <item><description>Sign-out records <c>SecurityAuditAction.Login</c>
///   with details.event="logout". The platform's
///   <see cref="SecurityAuditAction"/> enum lives under <c>platform/</c>
///   (off-limits per CLAUDE.md zone rules), so we don't add a Logout
///   member; instead we discriminate via the JSON details payload, which
///   the audit row already supports unstructured.</description></item>
/// </list>
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Name of the rate-limit policy applied to /login/submit.</summary>
    public const string LoginRateLimitPolicy = "login";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ── POST /login/submit ─────────────────────────────────────
        // Accept the form, verify, sign in, redirect.
        app.MapPost("/login/submit", LoginSubmitAsync)
            .AllowAnonymous()
            .DisableAntiforgery() // we validate manually via IAntiforgery
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithName("LoginSubmit");

        // ── POST /logout ───────────────────────────────────────────
        // Real sign-out. Requires authentication so an unauthenticated
        // GET can't trigger a side-effect-on-server log row, and so the
        // cookie scheme actually has something to sign out of.
        app.MapPost("/logout", LogoutPostAsync)
            .RequireAuthorization()
            .DisableAntiforgery() // POST form from MainLayout — token validated below
            .WithName("LogoutPost");

        // ── GET /logout ────────────────────────────────────────────
        // Convenience: GET → just bounce to /login. Anonymous in case
        // the user's cookie has already expired (otherwise they'd hit
        // the auth wall and the redirect chain would explode).
        app.MapGet("/logout", () => Results.Redirect("/login"))
            .AllowAnonymous()
            .WithName("LogoutGet");

        return app;
    }

    private static async Task<IResult> LoginSubmitAsync(
        HttpContext ctx,
        IAntiforgery antiforgery,
        IPasswordVerifier verifier,
        IdentityDbContext db,
        ISecurityAuditService audit,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        [FromForm] string? email,
        [FromForm] string? password,
        [FromForm] bool rememberMe = false,
        [FromForm] string? returnUrl = null)
    {
        var log = loggerFactory.CreateLogger("NickFinance.WebApp.Endpoints.Auth");

        // Anti-forgery — manual validation since DisableAntiforgery() is
        // set on the route. The endpoint is anonymous so the standard
        // middleware would skip, but we still want the token check (it
        // protects against CSRF from a logged-in CF Access user being
        // tricked into re-authing as a different operator).
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            log.LogWarning("Login submit failed anti-forgery validation.");
            return RedirectToLogin(returnUrl, "invalid", null);
        }

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return RedirectToLogin(returnUrl, "invalid", email);
        }

        var normalisedEmail = email.Trim().ToLowerInvariant();
        var verified = await verifier.VerifyAsync(normalisedEmail, password, ctx.RequestAborted);
        if (verified is null)
        {
            await TryAuditAsync(audit, SecurityAuditAction.Login, SecurityAuditResult.Denied,
                new { email = normalisedEmail, via = "cookie", reason = "invalid-credentials" });
            return RedirectToLogin(returnUrl, "invalid", normalisedEmail);
        }

        // Verified against NickHR — now confirm the matching
        // identity.users row exists. If not, we mint a clear "not
        // provisioned" message rather than a generic invalid-credentials
        // (the password WAS right; the missing piece is HR-side
        // provisioning).
        var tenantId = config.GetValue<long?>("NickFinance:DefaultTenantId") ?? 1L;
        var row = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == verified.Email && u.TenantId == tenantId, ctx.RequestAborted);
        if (row is null)
        {
            await TryAuditAsync(audit, SecurityAuditAction.Login, SecurityAuditResult.Denied,
                new { email = verified.Email, via = "cookie", reason = "not-provisioned" });
            return RedirectToLogin(returnUrl, "not-provisioned", verified.Email);
        }
        if (row.Status != UserStatus.Active)
        {
            await TryAuditAsync(audit, SecurityAuditAction.Login, SecurityAuditResult.Denied,
                new { email = verified.Email, via = "cookie", reason = "disabled", status = row.Status.ToString() });
            return RedirectToLogin(returnUrl, "disabled", verified.Email);
        }

        // ──── Sign in. Claims:
        //   email — used as the principal's name (Cookie's NameClaimType
        //     defaults to ClaimTypes.Name, but we match CF Access shape).
        //   sub   — internal_user_id stringified, so PersistentCurrentUserFactory
        //     can look the row up the same way it does for CF-Access principals.
        //   name  — display name pulled from NickHR's row, falls back to
        //     identity.users' display_name.
        //   nickerp.via=cookie — the SecurityAuditService discriminator.
        var displayName = !string.IsNullOrWhiteSpace(verified.DisplayName)
            ? verified.DisplayName!
            : (string.IsNullOrWhiteSpace(row.DisplayName) ? PrettyNameFromEmail(verified.Email) : row.DisplayName);

        var identity = new ClaimsIdentity(authenticationType: CfAccessAuth.CookieSchemeName,
            nameType: "email", roleType: ClaimTypes.Role);
        identity.AddClaim(new Claim("email", verified.Email));
        identity.AddClaim(new Claim("sub", row.InternalUserId.ToString()));
        identity.AddClaim(new Claim("name", displayName));
        identity.AddClaim(new Claim(LanTrustAuthHandler.ViaClaimType, "cookie"));

        var principal = new ClaimsPrincipal(identity);

        // Persistence:
        //   rememberMe ON  → 30 days, persistent cookie
        //   rememberMe OFF → 8 hours sliding (cookie auth's ExpireTimeSpan)
        var props = new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(30) : null,
            AllowRefresh = true,
        };

        await ctx.SignInAsync(CfAccessAuth.CookieSchemeName, principal, props);
        await TryAuditAsync(audit, SecurityAuditAction.Login, SecurityAuditResult.Allowed,
            new { email = verified.Email, via = "cookie", remember = rememberMe });

        var safeReturn = SanitizeReturnUrl(returnUrl);
        return Results.Redirect(safeReturn);
    }

    private static async Task<IResult> LogoutPostAsync(
        HttpContext ctx,
        IAntiforgery antiforgery,
        ISecurityAuditService audit)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            // Token mismatch — still sign them out (we don't want to
            // leave a half-authenticated user on the screen because of
            // a stale token), but skip the audit on the assumption the
            // POST may not have been the user's intent.
            await ctx.SignOutAsync(CfAccessAuth.CookieSchemeName);
            return Results.Redirect("/login");
        }

        await TryAuditAsync(audit, SecurityAuditAction.Login, SecurityAuditResult.Allowed,
            new { via = "cookie", @event = "logout" });
        await ctx.SignOutAsync(CfAccessAuth.CookieSchemeName);
        return Results.Redirect("/login");
    }

    private static IResult RedirectToLogin(string? returnUrl, string error, string? email)
    {
        var qs = $"?error={Uri.EscapeDataString(error)}";
        if (!string.IsNullOrEmpty(returnUrl))
        {
            qs += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
        }
        if (!string.IsNullOrEmpty(email))
        {
            qs += "&email=" + Uri.EscapeDataString(email);
        }
        return Results.Redirect("/login" + qs);
    }

    /// <summary>
    /// Same-origin guard for the post-login bounce. Without this, an
    /// attacker could craft <c>/login?returnUrl=https://evil/</c> and use
    /// the cookie sign-in as an open-redirect vector. We accept absolute-
    /// path URLs only ("/foo/bar"), reject everything else with "/".
    /// </summary>
    private static string SanitizeReturnUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "/";
        if (!raw.StartsWith('/')) return "/";
        if (raw.StartsWith("//")) return "/"; // protocol-relative, e.g. "//evil"
        return raw;
    }

    private static async Task TryAuditAsync(
        ISecurityAuditService audit,
        SecurityAuditAction action,
        SecurityAuditResult result,
        object details)
    {
        try
        {
            await audit.RecordAsync(action, targetType: "Login", targetId: null, result, details);
        }
        catch
        {
            // Audit must never break the user-facing flow. The service's
            // own try/catch already swallows DB errors, but defence in
            // depth here catches DI-resolution failures during the
            // anonymous-context window.
        }
    }

    private static string PrettyNameFromEmail(string email)
    {
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
}
