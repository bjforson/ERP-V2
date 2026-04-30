using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 13 / P2-FU-edge-auth — authenticates incoming
/// <c>/api/edge/replay</c> requests against per-edge-node API keys
/// stored in <c>audit.edge_node_api_keys</c>. Replaces (in parallel
/// during the rollout window) the Sprint 11 single-shared-secret
/// <c>X-Edge-Token</c> flow.
///
/// <para>
/// <b>Posture (strictly strengthening).</b> The Sprint 11 flow had:
/// <list type="bullet">
///   <item><description>One shared secret across all edges. Rotation
///   means simultaneously redeploying every edge.</description></item>
///   <item><description>The secret was stored in plaintext in
///   <c>EdgeNode:SharedSecret</c> config. A leaked appsettings file =
///   leaked all-edges credential.</description></item>
///   <item><description>No revocation path. To kill one edge's auth
///   you had to rotate the global secret.</description></item>
/// </list>
/// Sprint 13 flips all three: per-node keys (one edge breached =
/// one edge revoked), HMAC-hashed at rest (DB exfil yields hashes,
/// not usable keys), and per-row revocation via
/// <see cref="EdgeNodeApiKey.RevokedAt"/>.
/// </para>
///
/// <para>
/// <b>Hash construction.</b> Delegated to <see cref="EdgeKeyHasher"/>
/// in <c>Platform.Audit.Database</c> — same hash for the auth path
/// here as for the issuance path in
/// <see cref="NickERP.Platform.Audit.Database.EdgeNodeApiKeyService"/>.
/// The hasher's hash key is read from <c>EdgeAuth:HashKey</c> config;
/// when unset the hasher uses an <see cref="IEdgeKeyHashEnvelope"/>
/// fallback — production wires
/// <see cref="DataProtectionEdgeKeyHashEnvelope"/> below.
/// </para>
///
/// <para>
/// <b>Tenant context.</b> The handler runs BEFORE tenant resolution
/// (the request is unauthenticated until this passes), so the
/// initial DB lookup uses <see cref="ITenantContext.SetSystemContext"/>
/// to admit the cross-tenant scan. The migration's RLS policy has
/// the <c>OR app.tenant_id = '-1'</c> clause to admit this.
/// Registered in <c>docs/system-context-audit-register.md</c>.
/// </para>
///
/// <para>
/// <b>Legacy fallback.</b> If <see cref="LegacyTokenConfigKey"/> is
/// truthy in config, the handler also accepts the Sprint 11
/// <c>X-Edge-Token</c> header against the legacy
/// <c>EdgeNode:SharedSecret</c> value. Use of the legacy path logs
/// a warning so ops can drive the rollout window. Once every edge
/// has migrated to per-node keys, flip
/// <see cref="LegacyTokenConfigKey"/> to <c>false</c>.
/// </para>
/// </summary>
public sealed class EdgeAuthHandler
{
    /// <summary>HTTP header carrying the per-node API key (preferred).</summary>
    public const string ApiKeyHeader = "X-Edge-Api-Key";

    /// <summary>HTTP header carrying the legacy shared-secret token (fallback during rollout).</summary>
    public const string LegacyTokenHeader = "X-Edge-Token";

    /// <summary>HTTP header carrying a Bearer-style API key.</summary>
    public const string AuthorizationHeader = "Authorization";

    /// <summary>Configuration key — when truthy, the legacy <c>X-Edge-Token</c> path is admitted.</summary>
    public const string LegacyTokenConfigKey = "EdgeAuth:AllowLegacyToken";

    /// <summary>Configuration key — the Sprint 11 shared secret. Read only when legacy is enabled.</summary>
    public const string LegacySharedSecretConfigKey = "EdgeNode:SharedSecret";

    private readonly AuditDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IConfiguration _config;
    private readonly EdgeKeyHasher _hasher;
    private readonly ILogger<EdgeAuthHandler> _logger;
    private readonly TimeProvider _clock;

    public EdgeAuthHandler(
        AuditDbContext db,
        ITenantContext tenant,
        IConfiguration config,
        EdgeKeyHasher hasher,
        ILogger<EdgeAuthHandler> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Try to authenticate the request. Returns the auth result. Caller
    /// is responsible for short-circuiting to <c>401</c> on
    /// <see cref="EdgeAuthResult.Outcome"/> values other than
    /// <see cref="EdgeAuthOutcome.AuthenticatedPerNode"/> and
    /// <see cref="EdgeAuthOutcome.AuthenticatedLegacy"/>.
    /// </summary>
    public async Task<EdgeAuthResult> AuthenticateAsync(HttpContext http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        // 1. Per-node API key path (preferred). Try X-Edge-Api-Key first;
        //    fall back to Authorization: Bearer <key>.
        var presentedKey = ReadApiKey(http.Request);
        if (!string.IsNullOrEmpty(presentedKey))
        {
            var nodeResult = await TryAuthenticatePerNodeAsync(presentedKey, ct);
            if (nodeResult.Outcome == EdgeAuthOutcome.AuthenticatedPerNode)
            {
                return nodeResult;
            }

            // Per-node key was presented but didn't authenticate. Don't
            // silently fall through to the legacy path — that would
            // weaken the posture (an attacker with a guessed token
            // could bypass per-node by ALSO presenting a bad api key).
            // Instead, reject outright.
            _logger.LogWarning(
                "Edge auth rejected: per-node API key presented but did not authenticate (reason={Reason}, prefix={Prefix}).",
                nodeResult.Outcome, SafePrefix(presentedKey));
            return nodeResult;
        }

        // 2. Legacy X-Edge-Token fallback (if enabled in config).
        if (IsLegacyTokenAllowed())
        {
            var legacyResult = TryAuthenticateLegacy(http);
            if (legacyResult.Outcome == EdgeAuthOutcome.AuthenticatedLegacy)
            {
                _logger.LogWarning(
                    "Edge auth: legacy X-Edge-Token accepted. Migrate this edge to per-node X-Edge-Api-Key; flip EdgeAuth:AllowLegacyToken=false post-rollout.");
            }
            return legacyResult;
        }

        return new EdgeAuthResult(EdgeAuthOutcome.MissingCredential, null);
    }

    /// <summary>
    /// Attempt per-node authentication for a presented plaintext key.
    /// Public so the issuance flow can verify a freshly-issued key
    /// round-trips correctly.
    /// </summary>
    public async Task<EdgeAuthResult> TryAuthenticatePerNodeAsync(string presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return new EdgeAuthResult(EdgeAuthOutcome.MissingCredential, null);
        }

        var hash = _hasher.ComputeHash(presentedKey);

        // SetSystemContext: the lookup runs pre-tenant-resolution. The
        // RLS policy on edge_node_api_keys admits the read via the
        // OR app.tenant_id = '-1' clause.
        _tenant.SetSystemContext();

        EdgeNodeApiKey? row;
        try
        {
            row = await _db.EdgeNodeApiKeys.AsNoTracking()
                .Where(x => x.KeyHash == hash)
                .FirstOrDefaultAsync(ct);
        }
        catch (DbException ex)
        {
            _logger.LogError(ex, "Edge auth lookup failed.");
            return new EdgeAuthResult(EdgeAuthOutcome.LookupError, null);
        }

        if (row is null)
        {
            return new EdgeAuthResult(EdgeAuthOutcome.UnknownKey, null);
        }

        // Defence-in-depth constant-time comparison even though the DB
        // already did a bytea = match. If a future change moves the
        // lookup path to a non-unique index or a substring match, this
        // is the surface that still guarantees timing safety.
        if (!CryptographicOperations.FixedTimeEquals(row.KeyHash, hash))
        {
            return new EdgeAuthResult(EdgeAuthOutcome.UnknownKey, null);
        }

        if (row.RevokedAt is not null)
        {
            return new EdgeAuthResult(EdgeAuthOutcome.Revoked, row);
        }

        var now = _clock.GetUtcNow();
        if (row.ExpiresAt is { } exp && exp <= now)
        {
            return new EdgeAuthResult(EdgeAuthOutcome.Expired, row);
        }

        return new EdgeAuthResult(EdgeAuthOutcome.AuthenticatedPerNode, row);
    }

    private string? ReadApiKey(HttpRequest request)
    {
        // Prefer the explicit edge header; matches the X-Edge-Token
        // pattern the edge client already uses (just renamed).
        var direct = request.Headers[ApiKeyHeader].ToString();
        if (!string.IsNullOrEmpty(direct))
            return direct;

        // Bearer fallback — standard for newer clients. Strip the
        // scheme prefix; case-insensitive per RFC 7235.
        var auth = request.Headers[AuthorizationHeader].ToString();
        if (!string.IsNullOrEmpty(auth))
        {
            const string bearer = "Bearer ";
            if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            {
                var tail = auth[bearer.Length..].Trim();
                if (!string.IsNullOrEmpty(tail))
                    return tail;
            }
        }
        return null;
    }

    private bool IsLegacyTokenAllowed()
    {
        // Default true during rollout window. Spec: flip to false
        // post-rollout once every edge has migrated.
        var raw = _config[LegacyTokenConfigKey];
        if (string.IsNullOrEmpty(raw))
            return true;
        return bool.TryParse(raw, out var parsed) && parsed;
    }

    private EdgeAuthResult TryAuthenticateLegacy(HttpContext http)
    {
        var expected = _config[LegacySharedSecretConfigKey];
        if (string.IsNullOrEmpty(expected))
        {
            _logger.LogWarning(
                "Edge auth legacy path rejected: {ConfigKey} not configured. (Either configure the legacy secret or flip {Flag}=false to disable the legacy path entirely.)",
                LegacySharedSecretConfigKey, LegacyTokenConfigKey);
            return new EdgeAuthResult(EdgeAuthOutcome.MissingCredential, null);
        }

        var presented = http.Request.Headers[LegacyTokenHeader].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            return new EdgeAuthResult(EdgeAuthOutcome.MissingCredential, null);
        }

        var aBytes = Encoding.UTF8.GetBytes(expected);
        var bBytes = Encoding.UTF8.GetBytes(presented);
        if (CryptographicOperations.FixedTimeEquals(aBytes, bBytes))
        {
            return new EdgeAuthResult(EdgeAuthOutcome.AuthenticatedLegacy, null);
        }
        return new EdgeAuthResult(EdgeAuthOutcome.UnknownKey, null);
    }

    private static string SafePrefix(string presented)
    {
        if (string.IsNullOrEmpty(presented)) return "(empty)";
        return presented.Length <= EdgeKeyHasher.KeyPrefixLength
            ? presented
            : presented[..EdgeKeyHasher.KeyPrefixLength];
    }
}

/// <summary>Outcome categories from <see cref="EdgeAuthHandler.AuthenticateAsync"/>.</summary>
public enum EdgeAuthOutcome
{
    /// <summary>No header presented, or legacy path disabled and no per-node header.</summary>
    MissingCredential = 0,

    /// <summary>Per-node key presented, valid, not revoked, not expired.</summary>
    AuthenticatedPerNode = 1,

    /// <summary>Legacy X-Edge-Token presented + matched the configured shared secret.</summary>
    AuthenticatedLegacy = 2,

    /// <summary>Per-node key presented but no row matched (or wrong hash).</summary>
    UnknownKey = 3,

    /// <summary>Matching row found but <see cref="EdgeNodeApiKey.RevokedAt"/> is set.</summary>
    Revoked = 4,

    /// <summary>Matching row found but <see cref="EdgeNodeApiKey.ExpiresAt"/> has passed.</summary>
    Expired = 5,

    /// <summary>DB error during lookup. Treated as auth failure (fail-closed).</summary>
    LookupError = 6
}

/// <summary>Result of an edge-auth attempt. <see cref="MatchedRow"/> is non-null only when a per-node row was found (whether or not it was accepted).</summary>
public sealed record EdgeAuthResult(EdgeAuthOutcome Outcome, EdgeNodeApiKey? MatchedRow);

/// <summary>
/// Production <see cref="IEdgeKeyHashEnvelope"/> backed by ASP.NET Core
/// data protection. Derives a deterministic-per-host 32-byte hash key
/// when <c>EdgeAuth:HashKey</c> isn't set; clustered prod should set
/// the explicit config value because each host's data-protection key
/// ring is independent.
/// </summary>
public sealed class DataProtectionEdgeKeyHashEnvelope : IEdgeKeyHashEnvelope
{
    /// <summary>
    /// Data-protection purpose. Stable on disk; rotating means rotating
    /// every issued key.
    /// </summary>
    public const string DataProtectionPurpose = "edge-auth-hash-key-v1";

    private readonly IDataProtectionProvider _dataProtection;

    public DataProtectionEdgeKeyHashEnvelope(IDataProtectionProvider dataProtection)
    {
        _dataProtection = dataProtection ?? throw new ArgumentNullException(nameof(dataProtection));
    }

    public byte[] DeriveFallbackHashKey()
    {
        var protector = _dataProtection.CreateProtector(DataProtectionPurpose);
        var pattern = Encoding.UTF8.GetBytes("edge-auth-hash-key-derivation-v1");
        var enc = protector.Protect(pattern);
        return SHA256.HashData(enc);
    }
}
