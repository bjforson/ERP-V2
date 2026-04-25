namespace NickERP.Platform.Identity.Auth;

/// <summary>
/// Configuration for the NickERP CF Access authentication scheme. Bound from
/// <c>NickErp:Identity:CfAccess</c> in <c>appsettings.json</c> + env-var overrides.
/// </summary>
public sealed class CfAccessAuthenticationOptions
{
    /// <summary>Configuration section path that binds these options.</summary>
    public const string SectionName = "NickErp:Identity:CfAccess";

    /// <summary>The auth scheme name registered against ASP.NET Core authentication.</summary>
    public const string SchemeName = "NickErp.Identity";

    /// <summary>
    /// CF team subdomain — e.g. <c>nickscan</c>. Used to compute the issuer
    /// (<c>https://nickscan.cloudflareaccess.com</c>) and the JWKS endpoint
    /// (<c>https://nickscan.cloudflareaccess.com/cdn-cgi/access/certs</c>).
    /// Required.
    /// </summary>
    public string TeamDomain { get; set; } = string.Empty;

    /// <summary>
    /// CF Access Application Audience tag (the <c>aud</c> claim CF Access
    /// mints into every token issued for this app). Lives in the CF Access
    /// dashboard under each Application's settings. Required.
    /// </summary>
    public string ApplicationAudience { get; set; } = string.Empty;

    /// <summary>
    /// Fall back to <c>Authorization: Bearer &lt;jwt&gt;</c> when the
    /// <c>Cf-Access-Jwt-Assertion</c> header is absent. Useful for local
    /// testing with curl. Defaults to <see langword="true"/>.
    /// </summary>
    public bool AcceptAuthorizationHeaderFallback { get; set; } = true;

    /// <summary>
    /// Allowed clock skew when validating <c>exp</c> / <c>nbf</c>. Default
    /// 30 seconds. CF Access tokens are short-lived; large skew is a
    /// security regression — keep this low.
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>Settings for the Development-only bypass scheme.</summary>
    public DevBypassOptions DevBypass { get; set; } = new();

    /// <summary>Computed CF issuer URL.</summary>
    public string Issuer => $"https://{TeamDomain}.cloudflareaccess.com";

    /// <summary>Computed CF JWKS URL.</summary>
    public string JwksUrl => $"https://{TeamDomain}.cloudflareaccess.com/cdn-cgi/access/certs";

    /// <summary>Throws if required fields are missing or look obviously wrong.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TeamDomain))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TeamDomain is required (the CF Access team subdomain, e.g. 'nickscan').");
        }

        if (TeamDomain.Contains("://", StringComparison.Ordinal) || TeamDomain.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TeamDomain must be just the subdomain ('nickscan'), not a URL.");
        }

        if (string.IsNullOrWhiteSpace(ApplicationAudience))
        {
            throw new InvalidOperationException(
                $"{SectionName}:ApplicationAudience is required (the CF Access application AUD tag).");
        }

        if (ClockSkewSeconds < 0 || ClockSkewSeconds > 300)
        {
            throw new InvalidOperationException(
                $"{SectionName}:ClockSkewSeconds must be between 0 and 300; got {ClockSkewSeconds}.");
        }

        DevBypass.Validate();
    }
}

/// <summary>
/// Dev-mode bypass — when enabled, accepts a synthetic identity from a
/// header instead of validating a real CF Access JWT. Used for local
/// development and integration tests so engineers don't need to configure
/// CF Access locally. MUST never be enabled outside <c>Development</c>
/// environments (the registration extension throws at startup if this
/// invariant is violated).
/// </summary>
public sealed class DevBypassOptions
{
    /// <summary>Enable the dev bypass.</summary>
    public bool Enabled { get; set; }

    /// <summary>Email of the synthetic dev user. Used to look up a real <c>IdentityUser</c> via the resolver — bypass skips JWT validation, NOT user provisioning.</summary>
    public string FakeUserEmail { get; set; } = "dev@nickscan.com";

    /// <summary>The HTTP header that triggers the bypass when <see cref="Enabled"/> is true. Defaults to <c>X-Dev-User</c>. May carry an email override.</summary>
    public string TriggerHeader { get; set; } = "X-Dev-User";

    /// <summary>Throws if dev-bypass config is internally inconsistent.</summary>
    public void Validate()
    {
        if (Enabled && string.IsNullOrWhiteSpace(FakeUserEmail))
        {
            throw new InvalidOperationException(
                $"{CfAccessAuthenticationOptions.SectionName}:DevBypass:FakeUserEmail is required when DevBypass is enabled.");
        }
    }
}
