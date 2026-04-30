using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Authenticates requests originating from a configured LAN CIDR allowlist
/// as a single shared "trusted-LAN" identity. Lets office-on-site users
/// hit the box directly (e.g. <c>http://10.0.1.254:5500/</c>) without going
/// through the CF Access OTP flow on every session.
///
/// <para>Trade-off:</para>
/// <list type="bullet">
///   <item>Pro: zero-friction LAN UX. Resilient to internet/CF outage.</item>
///   <item>Con: every LAN request is attributed to the same shared user.
///   Per-user audit on the LAN path is impossible without device-level
///   credentials (cookie / API key / AD integrated).</item>
/// </list>
///
/// <para>Activation:</para>
/// <list type="bullet">
///   <item><c>NICKFINANCE_LAN_TRUST_EMAIL</c> — the shared identity (must
///   exist in <c>identity.users</c>; pre-create or be willing to lazy-create).
///   Unset → handler always returns NoResult and the request 401s.</item>
///   <item><c>NICKFINANCE_LAN_TRUST_NETWORKS</c> — comma-separated CIDRs
///   (e.g. <c>10.0.0.0/21</c>). Unset → no LAN is trusted; handler is inert.</item>
/// </list>
///
/// <para>Coexistence with Cloudflare Access:</para>
/// CF Access still owns external traffic via <c>Cf-Access-Jwt-Assertion</c>.
/// The smart-scheme selector in <see cref="CfAccessAuth"/> forwards each
/// request to whichever scheme can handle it (JWT present → CF Access;
/// else trusted source IP → LAN-trust; else neither → 401).
/// </summary>
public sealed class LanTrustAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "LanTrust";
    public const string ViaClaimType = "nickerp.via";
    public const string ViaClaimValue = "lan-trust";

    public LanTrustAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var trustedEmail = Environment.GetEnvironmentVariable("NICKFINANCE_LAN_TRUST_EMAIL");
        var rawNetworks = Environment.GetEnvironmentVariable("NICKFINANCE_LAN_TRUST_NETWORKS");

        if (string.IsNullOrWhiteSpace(trustedEmail) || string.IsNullOrWhiteSpace(rawNetworks))
        {
            // Feature off — no email or no networks configured. Let other
            // schemes try.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var remote = Context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        // IPv4-mapped IPv6 (::ffff:10.0.1.5) → unwrap so CIDR comparison works
        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        var trusted = false;
        foreach (var token in rawNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPNetwork.TryParse(token, out var net) && net.Contains(remote))
            {
                trusted = true;
                break;
            }
        }
        if (!trusted)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Manufacture a CF-Access-shaped principal so the rest of the
        // pipeline (PersistentCurrentUserFactory, RoleService, SecurityAudit)
        // doesn't care which scheme authenticated.
        var email = trustedEmail.ToLowerInvariant();
        var displayName = PrettyNameFromEmail(email);
        var deterministicSub = "lan-trust:" + email;

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("email", email),
                new Claim("name", displayName),
                new Claim("sub", deterministicSub),
                new Claim(ViaClaimType, ViaClaimValue),
                new Claim(ViaClaimType + ".source-ip", remote.ToString()),
            },
            authenticationType: SchemeName,
            nameType: "email",
            roleType: ClaimTypes.Role);

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        Logger.LogDebug("LAN-trust authenticated {Email} from {Source}", email, remote);
        return Task.FromResult(AuthenticateResult.Success(ticket));
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
