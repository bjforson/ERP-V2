using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NickERP.Platform.Identity.Auth;

/// <summary>
/// Validates a CF Access JWT against the team's JWKS endpoint. Returns the
/// <see cref="ClaimsPrincipal"/> on success, <see langword="null"/> on any
/// validation failure (signature, expiry, issuer, audience, malformed token).
/// Failure reasons are logged at <c>Warning</c> level so failed sign-in
/// attempts are visible in Seq.
/// </summary>
public interface ICfJwtValidator
{
    Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="ICfJwtValidator"/> implementation. Singleton — caches a
/// <see cref="ConfigurationManager{T}"/> that auto-refreshes JWKS on TTL or
/// on missed key id.
/// </summary>
internal sealed class CfJwtValidator : ICfJwtValidator
{
    private readonly CfAccessAuthenticationOptions _options;
    private readonly ILogger<CfJwtValidator> _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager;
    private readonly JwtSecurityTokenHandler _handler = new();

    public CfJwtValidator(CfAccessAuthenticationOptions options, ILogger<CfJwtValidator> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();

        _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress: _options.JwksUrl,
            configRetriever: new JwksOnlyConfigurationRetriever(),
            docRetriever: new HttpDocumentRetriever { RequireHttps = true });

        _handler.MapInboundClaims = false; // keep CF Access claim names ('sub', 'email') as-is
    }

    public async Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        OpenIdConnectConfiguration config;
        try
        {
            config = await _configManager.GetConfigurationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch CF Access JWKS from {JwksUrl}", _options.JwksUrl);
            return null;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.ApplicationAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_options.ClockSkewSeconds),
            NameClaimType = "email"
        };

        try
        {
            var principal = _handler.ValidateToken(token, parameters, out _);
            return principal;
        }
        catch (SecurityTokenExpiredException ex)
        {
            _logger.LogDebug(ex, "CF Access JWT expired");
            return null;
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "CF Access JWT validation failed: {Reason}", ex.Message);
            return null;
        }
        catch (ArgumentException ex)
        {
            // Malformed token (e.g. not three dot-separated segments)
            _logger.LogWarning(ex, "CF Access JWT was malformed");
            return null;
        }
    }
}

/// <summary>
/// <see cref="IConfigurationRetriever{T}"/> that reads a raw JWKS document
/// (CF Access serves <c>/cdn-cgi/access/certs</c> as JWKS without an OIDC
/// discovery wrapper) and surfaces it as an
/// <see cref="OpenIdConnectConfiguration"/> shaped object — the only shape
/// <see cref="ConfigurationManager{T}"/> accepts. Issuer and other discovery
/// fields are left empty since validation goes through
/// <see cref="TokenValidationParameters"/> not the configuration object.
/// </summary>
internal sealed class JwksOnlyConfigurationRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken cancel)
    {
        var doc = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var jwks = new JsonWebKeySet(doc);

        var config = new OpenIdConnectConfiguration();
        foreach (var key in jwks.GetSigningKeys())
        {
            config.SigningKeys.Add(key);
        }
        foreach (var key in jwks.Keys)
        {
            config.JsonWebKeySet ??= new JsonWebKeySet();
            config.JsonWebKeySet.Keys.Add(key);
        }
        return config;
    }
}
