using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace NickERP.Perf.Tests.Auth;

/// <summary>
/// Sprint 52 / FU-perf-auth-mocking-decision (Sprint 30) — outbound
/// JWT signer for the NBomber HTTP client. Produces signed-but-
/// real-CF-Access-equivalent JWTs against a known-public-key pair
/// generated at process start.
/// </summary>
/// <remarks>
/// <para>
/// Test-plan §11 left "auth latency in tests — mock vs real" as an
/// open question. Sprint 52 commits to: <b>mock JWKS validation for
/// rep-volume tests + spot-check with real auth</b>. This class is the
/// mock-side primitive; the spot-check path uses a real CF Access JWT
/// produced out-of-band by an operator login, fed into NBomber via
/// <c>NICKERP_PERF_BEARER_TOKEN</c> env var.
/// </para>
/// <para>
/// Why not pre-bake the signing key on disk: rotating per process run
/// keeps the perf rig from leaking a long-lived signing key into git
/// history if a test artifact is ever attached to a PR. The trade-off
/// is that the API host running the perf scenario must trust this
/// per-run key — done via the <c>MockJwksEndpoint</c> companion
/// (followup to this work; the per-run key is exposed at a
/// well-known URL on the perf-rig host's loopback for the API to
/// validate against).
/// </para>
/// <para>
/// This class is intentionally <b>not</b> a real
/// <c>DelegatingHandler</c> — NBomber's HTTP client is configured
/// per-scenario via <c>HttpClient.DefaultRequestHeaders</c>; the
/// scenario calls <see cref="ProduceBearerToken"/> once, sets the
/// header, and reuses for the run. The "Handler" naming aligns with
/// the brief; the behaviour is "produce + cache one token per run."
/// </para>
/// </remarks>
public sealed class MockJwtBearerHandler : IDisposable
{
    /// <summary>
    /// CF Access's default issuer shape:
    /// <c>https://&lt;team&gt;.cloudflareaccess.com</c>. The mock issues
    /// from this URL so a real CF-Access-shaped validator only needs
    /// the JWKS endpoint pointed at the mock host.
    /// </summary>
    public const string DefaultIssuer = "https://nickerp-perf.cloudflareaccess.com";

    /// <summary>
    /// Default audience claim for perf scenarios. Matches the
    /// <c>NickErp:Identity:CfAccess:ApplicationAud</c> shape.
    /// </summary>
    public const string DefaultAudience = "perf-test-app";

    private readonly RsaSecurityKey _signingKey;
    private readonly SigningCredentials _signingCredentials;
    private readonly JwtSecurityTokenHandler _handler;
    private readonly RSA _rsa;
    private readonly TimeProvider _clock;

    public string Issuer { get; }
    public string Audience { get; }

    /// <summary>
    /// Stable kid (JWKS key-id) for this run. Surfaced to the API-side
    /// JWKS-mock so the validator's kid lookup hits.
    /// </summary>
    public string KeyId { get; }

    public MockJwtBearerHandler(
        string issuer = DefaultIssuer,
        string audience = DefaultAudience,
        TimeProvider? clock = null)
    {
        Issuer = issuer;
        Audience = audience;
        _clock = clock ?? TimeProvider.System;
        _rsa = RSA.Create(2048);
        KeyId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _signingKey = new RsaSecurityKey(_rsa) { KeyId = KeyId };
        _signingCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256);
        _handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
    }

    /// <summary>
    /// Returns the JWKS-shaped public key for this run, suitable for
    /// serving from a mock JWKS endpoint that the API host will fetch.
    /// </summary>
    public JsonWebKey GetPublicJsonWebKey()
        => JsonWebKeyConverter.ConvertFromRSASecurityKey(
            new RsaSecurityKey(_rsa.ExportParameters(includePrivateParameters: false)) { KeyId = KeyId });

    /// <summary>
    /// Produce a CF-Access-shaped JWT for the supplied user. Tokens are
    /// produced in milliseconds and cached by the caller per-run; the
    /// signing cost dominates so issuing a fresh one per request is
    /// counterproductive.
    /// </summary>
    /// <param name="subject">Stable subject id — matches CF Access's <c>sub</c> claim.</param>
    /// <param name="email">User email — matches CF Access's <c>email</c> claim.</param>
    /// <param name="tenantId">Tenant id added as a custom <c>tenant_id</c> claim.</param>
    /// <param name="extraClaims">Optional extra claims (e.g. roles). Kept narrow.</param>
    /// <param name="lifetime">Token lifetime; defaults to 24 h (matches CF Access default).</param>
    public string ProduceBearerToken(
        string subject,
        string email,
        long tenantId,
        IReadOnlyDictionary<string, string>? extraClaims = null,
        TimeSpan? lifetime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var now = _clock.GetUtcNow();
        var expires = now.Add(lifetime ?? TimeSpan.FromHours(24));

        var claims = new List<Claim>
        {
            new("sub", subject),
            new("email", email),
            new("tenant_id", tenantId.ToString()),
            new("iat", now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("nbf", now.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };
        if (extraClaims is not null)
        {
            foreach (var kv in extraClaims)
                claims.Add(new Claim(kv.Key, kv.Value));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: _signingCredentials);

        return _handler.WriteToken(token);
    }

    public void Dispose() => _rsa.Dispose();
}
