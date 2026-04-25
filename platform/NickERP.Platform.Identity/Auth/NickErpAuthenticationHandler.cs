using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NickERP.Platform.Identity.Services;

namespace NickERP.Platform.Identity.Auth;

/// <summary>
/// ASP.NET Core <see cref="AuthenticationHandler{T}"/> wrapping
/// <see cref="IIdentityResolver"/>. Lets modules use standard
/// <c>[Authorize]</c> attributes; the handler delegates to the resolver
/// internally so the IDENTITY.md contract ("one resolver call per request")
/// remains the single source of truth.
/// </summary>
public sealed class NickErpAuthenticationHandler : AuthenticationHandler<NickErpAuthenticationHandler.SchemeOptions>
{
    public NickErpAuthenticationHandler(
        IOptionsMonitor<SchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder)
        : base(options, loggerFactory, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var resolver = Context.RequestServices.GetService(typeof(IIdentityResolver)) as IIdentityResolver;
        if (resolver is null)
        {
            return AuthenticateResult.Fail("IIdentityResolver is not registered. Call AddNickErpIdentity in Program.cs.");
        }

        var resolved = await resolver.ResolveAsync(Context, Context.RequestAborted);
        if (resolved is null)
        {
            return AuthenticateResult.NoResult();
        }

        var identity = new ClaimsIdentity(authenticationType: CfAccessAuthenticationOptions.SchemeName);
        identity.AddClaim(new Claim(NickErpClaims.Id, resolved.Id.ToString()));
        identity.AddClaim(new Claim(NickErpClaims.DisplayName, resolved.DisplayName));
        identity.AddClaim(new Claim(NickErpClaims.TenantId, resolved.TenantId.ToString()));
        identity.AddClaim(new Claim(NickErpClaims.IsServiceToken, resolved.IsServiceToken ? "true" : "false"));

        if (!string.IsNullOrEmpty(resolved.Email))
        {
            identity.AddClaim(new Claim(NickErpClaims.Email, resolved.Email));
            identity.AddClaim(new Claim(ClaimTypes.Email, resolved.Email));
        }

        if (!string.IsNullOrEmpty(resolved.ExternalSubject))
        {
            identity.AddClaim(new Claim(NickErpClaims.ExternalSubject, resolved.ExternalSubject));
        }

        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resolved.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, resolved.DisplayName));

        foreach (var scope in resolved.ScopeCodes)
        {
            identity.AddClaim(new Claim(NickErpClaims.Scope, scope));
            // Mirror as Role so [Authorize(Roles="Finance.PettyCash.Approver")] works.
            identity.AddClaim(new Claim(ClaimTypes.Role, scope));
        }

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>Empty scheme options — all configuration is on <see cref="CfAccessAuthenticationOptions"/>.</summary>
    public sealed class SchemeOptions : AuthenticationSchemeOptions { }
}
