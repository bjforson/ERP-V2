using Microsoft.AspNetCore.Http;

namespace NickERP.Platform.Identity.Services;

/// <summary>
/// The single auth surface for every NickERP service. Reads the appropriate
/// inbound headers, validates them, and returns a <see cref="ResolvedIdentity"/>
/// the application can authorize against. Apps NEVER parse JWTs themselves.
/// </summary>
/// <remarks>
/// <para>
/// Resolution paths in order:
/// <list type="number">
///   <item><description><see cref="Auth.CfAccessAuthenticationOptions.DevBypass"/> header (Development only) — synthetic identity, still hits the DB to load the real user's scopes.</description></item>
///   <item><description><c>Cf-Access-Jwt-Assertion</c> header — Cloudflare Access JWT; validated against CF JWKS. <c>sub</c> claim looked up against <see cref="Entities.ServiceTokenIdentity.TokenClientId"/> first; if no match, <c>email</c> claim looked up against <see cref="Entities.IdentityUser.NormalizedEmail"/>.</description></item>
///   <item><description><c>Authorization: Bearer &lt;jwt&gt;</c> header — same JWT validation, fall-back path for local testing.</description></item>
/// </list>
/// </para>
///
/// <para>Returns <see langword="null"/> when no path applies, when JWT validation fails, or when the resolved subject is not provisioned / has been deactivated. The caller should treat <see langword="null"/> as 401.</para>
/// </remarks>
public interface IIdentityResolver
{
    /// <summary>Resolve the caller of the current request, or <see langword="null"/> if no valid identity can be established.</summary>
    Task<ResolvedIdentity?> ResolveAsync(HttpContext ctx, CancellationToken ct = default);
}
