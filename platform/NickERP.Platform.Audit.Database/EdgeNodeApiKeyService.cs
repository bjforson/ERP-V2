using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit.Database.Entities;

namespace NickERP.Platform.Audit.Database;

/// <summary>
/// Sprint 13 / P2-FU-edge-auth — admin-action issuance + revocation
/// flow for per-edge-node API keys. Sibling to the IcumsKeyRotation-
/// Service in <c>NickERP.Inspection.Web.Services</c>; both share the
/// "operator-driven, never automated, plaintext only at issue time"
/// posture.
///
/// <para>
/// Lives in <c>Platform.Audit.Database</c> (alongside the entity)
/// rather than in any host so the admin UI in <c>apps/portal</c> can
/// consume it without taking a dependency on
/// <c>NickERP.Inspection.Web</c>.
/// </para>
///
/// <para>
/// <b>Issuance returns plaintext exactly once.</b>
/// <see cref="IssueAsync"/> generates a fresh CSPRNG plaintext key,
/// hashes it via <see cref="EdgeKeyHasher.ComputeHash"/>, stores
/// the hash + prefix, and returns the plaintext to the caller. The
/// caller (admin Razor page) MUST surface the plaintext to the
/// operator and warn that it cannot be retrieved later. We
/// deliberately do not echo the plaintext in any log line — the only
/// trace of issuance is the row's <see cref="EdgeNodeApiKey.KeyPrefix"/>
/// + <see cref="EdgeNodeApiKey.IssuedAt"/>.
/// </para>
///
/// <para>
/// <b>Revocation is idempotent + immutable.</b> Once
/// <see cref="EdgeNodeApiKey.RevokedAt"/> is set the row is
/// considered fully retired; <see cref="RevokeAsync"/> is a no-op
/// on already-revoked keys (and returns <c>false</c>). Rows are
/// NEVER deleted — the migration explicitly REVOKEs DELETE on the
/// table.
/// </para>
///
/// <para>
/// <b>Tenancy.</b> Each method takes the tenant id explicitly. RLS
/// narrows reads/writes to that tenant; the explicit
/// <c>WHERE TenantId = ...</c> filter keeps the SQL plan tight and
/// makes the intent obvious.
/// </para>
/// </summary>
public sealed class EdgeNodeApiKeyService
{
    private readonly AuditDbContext _db;
    private readonly EdgeKeyHasher _hasher;
    private readonly ILogger<EdgeNodeApiKeyService> _logger;
    private readonly TimeProvider _clock;

    public EdgeNodeApiKeyService(
        AuditDbContext db,
        EdgeKeyHasher hasher,
        ILogger<EdgeNodeApiKeyService> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Issue a fresh API key for the given edge node + tenant. Returns
    /// the plaintext key + the new row's id; the plaintext is shown to
    /// the operator EXACTLY ONCE.
    /// </summary>
    public async Task<EdgeKeyIssuance> IssueAsync(
        long tenantId,
        string edgeNodeId,
        string? description = null,
        DateTimeOffset? expiresAt = null,
        Guid? createdByUserId = null,
        CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeNodeId);
        if (edgeNodeId.Length > 100)
            throw new ArgumentException("EdgeNodeId must be at most 100 chars.", nameof(edgeNodeId));
        if (description is { Length: > 200 })
            throw new ArgumentException("Description must be at most 200 chars.", nameof(description));
        var now = _clock.GetUtcNow();
        if (expiresAt is { } e && e <= now)
            throw new ArgumentException("ExpiresAt must be in the future.", nameof(expiresAt));

        var plaintext = EdgeKeyHasher.GenerateApiKey();
        var hash = _hasher.ComputeHash(plaintext);
        var prefix = EdgeKeyHasher.ComputePrefix(plaintext);

        var row = new EdgeNodeApiKey
        {
            TenantId = tenantId,
            EdgeNodeId = edgeNodeId,
            KeyHash = hash,
            KeyPrefix = prefix,
            IssuedAt = now,
            ExpiresAt = expiresAt,
            RevokedAt = null,
            Description = description,
            CreatedByUserId = createdByUserId
        };

        _db.EdgeNodeApiKeys.Add(row);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Edge API key issued: edge={EdgeNodeId} tenant={TenantId} prefix={Prefix} expires={Expires} createdBy={UserId}.",
            edgeNodeId, tenantId, prefix, expiresAt, createdByUserId);

        return new EdgeKeyIssuance(row.Id, plaintext, prefix, row.IssuedAt, row.ExpiresAt);
    }

    /// <summary>
    /// Revoke the key with the given <paramref name="keyId"/> for the
    /// given tenant. Returns <c>true</c> if the row transitioned from
    /// active to revoked, <c>false</c> if it was already revoked or
    /// not found (idempotent).
    /// </summary>
    public async Task<bool> RevokeAsync(long tenantId, Guid keyId, CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        var row = await _db.EdgeNodeApiKeys
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.Id == keyId, ct);
        if (row is null)
        {
            _logger.LogWarning("Edge API key revoke: row not found (tenant={TenantId} keyId={KeyId}).", tenantId, keyId);
            return false;
        }
        if (row.RevokedAt is not null)
        {
            return false;
        }

        row.RevokedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Edge API key revoked: edge={EdgeNodeId} tenant={TenantId} prefix={Prefix} keyId={KeyId}.",
            row.EdgeNodeId, tenantId, row.KeyPrefix, keyId);

        return true;
    }

    /// <summary>
    /// List API keys for the given tenant. Optionally narrowed to a
    /// single edge node. Metadata only — never the hash, prefix only.
    /// </summary>
    public async Task<IReadOnlyList<EdgeKeySummary>> ListAsync(
        long tenantId,
        string? edgeNodeId = null,
        CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        var query = _db.EdgeNodeApiKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId);
        if (!string.IsNullOrEmpty(edgeNodeId))
            query = query.Where(k => k.EdgeNodeId == edgeNodeId);

        return await query
            .OrderByDescending(k => k.IssuedAt)
            .Select(k => new EdgeKeySummary(
                k.Id,
                k.EdgeNodeId,
                k.KeyPrefix,
                k.IssuedAt,
                k.ExpiresAt,
                k.RevokedAt,
                k.Description,
                k.CreatedByUserId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// List edge node ids that have any authorization rows. Used by the
    /// admin UI to populate a "pick edge" dropdown — the operator
    /// can issue a key only for edges that are already authorized
    /// (the auth path needs both the api key AND an authorization
    /// row).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListAuthorizedEdgeNodesAsync(long tenantId, CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        return await _db.EdgeNodeAuthorizations.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.EdgeNodeId)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(ct);
    }
}

/// <summary>
/// Outcome of <see cref="EdgeNodeApiKeyService.IssueAsync"/>. The
/// <see cref="Plaintext"/> field is the only place the plaintext key
/// will ever surface — the caller MUST display it to the operator
/// once and not store it.
/// </summary>
public sealed record EdgeKeyIssuance(
    Guid KeyId,
    string Plaintext,
    string Prefix,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>One key row's metadata for operator UIs. No hash, plaintext, or anything that could authenticate.</summary>
public sealed record EdgeKeySummary(
    Guid KeyId,
    string EdgeNodeId,
    string KeyPrefix,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? Description,
    Guid? CreatedByUserId);
