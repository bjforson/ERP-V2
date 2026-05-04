using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NickHR.Infrastructure.Data;

namespace NickHR.API.Security;

/// <summary>
/// Single-session enforcement for NickHR (mirrors the NSCIM_API validator).
///
/// Protocol:
/// 1. <c>AuthService.GenerateJwtToken</c> stamps each new token with
///    <c>sid = ApplicationUser.CurrentSessionId</c>.
/// 2. <c>LoginAsync</c> / <c>RegisterAsync</c> rotate <c>CurrentSessionId</c>
///    BEFORE minting the new JWT, then invalidate the cache key so this
///    validator re-reads from the DB on the very next request.
/// 3. <c>RefreshTokenAsync</c> intentionally does NOT rotate — refreshes
///    re-mint with the same sid so they don't kick a user's other devices.
/// 4. Every authenticated request re-runs <see cref="ValidateAsync"/>; if the
///    incoming <c>sid</c> claim doesn't match the canonical column, the auth
///    fails and the user is forced back to login.
///
/// NickHR specifics vs NSCIM:
/// - <c>ApplicationUser.Id</c> is a string (default ASP.NET Identity), not int.
/// - There is no <c>IUserRepository</c>; we read the sid directly from the
///   <c>NickHRDbContext</c> with <c>AsNoTracking</c> for a hot-path-friendly
///   single-column projection.
///
/// Carve-outs: NickHR has no signed-URL middleware (yet). If one is added,
/// mirror the NSCIM <c>auth_method == "signed-url"</c> bypass here.
///
/// Caching: 30s TTL in <see cref="IMemoryCache"/> under <c>"sid:{userId}"</c>.
/// AuthService MUST <c>cache.Remove("sid:{userId}")</c> after every rotation.
/// </summary>
public static class SingleSessionValidator
{
    private const string CacheKeyPrefix = "sid:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public static async Task<bool> ValidateAsync(HttpContext httpContext, ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return true;

        // GOTCHA: JwtBearerHandler defaults MapInboundClaims = true, which routes
        // the JWT "sid" short name through Microsoft.IdentityModel's
        // DefaultInboundClaimTypeMap and renames it to ClaimTypes.Sid
        // ("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/sid"). So a
        // bare FindFirst("sid") returns null and the validator would reject
        // every token. Look up BOTH names — works regardless of mapping config.
        var sidClaim = principal.FindFirst(ClaimTypes.Sid)?.Value
                       ?? principal.FindFirst("sid")?.Value;
        var userIdClaim =
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;

        if (string.IsNullOrEmpty(sidClaim) || string.IsNullOrEmpty(userIdClaim))
        {
            // Tokens issued before single-session shipped lack the sid claim.
            // Force re-login so the next mint stamps a sid.
            return false;
        }

        var sp = httpContext.RequestServices;
        var cache = sp.GetService<IMemoryCache>();
        var db = sp.GetService<NickHRDbContext>();
        if (db == null) return true; // best-effort — don't lock everyone out on misconfig

        var cacheKey = CacheKeyPrefix + userIdClaim;
        Guid? canonical;
        if (cache != null && cache.TryGetValue<Guid?>(cacheKey, out var cached))
        {
            canonical = cached;
        }
        else
        {
            canonical = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userIdClaim)
                .Select(u => (Guid?)u.CurrentSessionId)
                .FirstOrDefaultAsync();

            if (cache != null)
            {
                // If IMemoryCache is configured with a SizeLimit (NSCIM does this),
                // every entry must specify a Size — otherwise SetEntry throws.
                // Defensive try/catch keeps a misconfigured cache from breaking
                // auth entirely; we just pay the DB hit on every request.
                try
                {
                    cache.Set(cacheKey, canonical, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = CacheTtl,
                        Size = 1
                    });
                }
                catch (InvalidOperationException)
                {
                    // Swallow — auth flow continues with a fresh DB read each time.
                }
            }
        }

        if (!canonical.HasValue) return false;
        return string.Equals(canonical.Value.ToString(), sidClaim, StringComparison.OrdinalIgnoreCase);
    }
}
