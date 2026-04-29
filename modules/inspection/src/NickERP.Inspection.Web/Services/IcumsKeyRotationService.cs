using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 9 / FU-icums-signing — admin-action rotation flow for
/// per-tenant IcumsGh signing keys.
///
/// <para>
/// <b>Not a worker.</b> Rotation is operator-initiated, invoked
/// through the admin endpoints in
/// <c>IcumsKeyRotationEndpoint</c>. Background-driven rotation is
/// out of scope for v0; a future "rotate all tenants on a schedule"
/// service can iterate over <c>tenancy.tenants</c> using
/// <c>SetSystemContext()</c> + a per-tenant call into this service.
/// </para>
///
/// <para>
/// <b>Two-phase rotation.</b>
/// <list type="number">
///   <item><description><see cref="GenerateAsync"/> creates a new
///   key (CSPRNG → wrap → INSERT) but does NOT activate it. The
///   admin captures the new <c>keyId</c> for the audit
///   trail.</description></item>
///   <item><description><see cref="ActivateAsync"/> flips state:
///   sets <c>ActivatedAt = now</c> on the new key,
///   <c>RetiredAt = now</c> on the previously-active key, and
///   <c>VerificationOnlyUntil = now + verificationWindow</c> on
///   the retired key. The verifier accepts the old key for
///   incoming signatures during this overlap; the signer uses
///   only the new key.</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Tenant scope.</b> Per-tenant rotation. Tenant context is
/// resolved from the request (RLS narrows the SELECT/INSERT to
/// the calling tenant); the caller cannot rotate another tenant's
/// keys via these methods. A future cross-tenant rotation flow
/// would need <c>SetSystemContext()</c> — call out in the audit
/// register if/when added.
/// </para>
/// </summary>
public sealed class IcumsKeyRotationService
{
    /// <summary>Default verification overlap window when ops doesn't specify a value.</summary>
    public static readonly TimeSpan DefaultVerificationWindow = TimeSpan.FromDays(7);

    private readonly InspectionDbContext _db;
    private readonly IcumsHmacEnvelopeSigner _signer;
    private readonly ILogger<IcumsKeyRotationService> _logger;

    public IcumsKeyRotationService(
        InspectionDbContext db,
        IcumsHmacEnvelopeSigner signer,
        ILogger<IcumsKeyRotationService> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generate + store a new (inactive) signing key for the given
    /// tenant. The key id is auto-numbered <c>k1</c>, <c>k2</c>, ...
    /// based on the existing rows for the tenant. The new key is NOT
    /// activated; call <see cref="ActivateAsync"/> to make it the
    /// signer's preferred key.
    /// </summary>
    /// <returns>The new row's <c>KeyId</c>.</returns>
    public async Task<string> GenerateAsync(long tenantId, CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        // Determine the next key id by scanning existing keys for this
        // tenant. RLS narrows to the caller's tenant; the explicit
        // Where keeps the SQL plan tight.
        var existingIds = await _db.IcumsSigningKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .Select(k => k.KeyId)
            .ToListAsync(ct);

        var next = NextKeyId(existingIds);

        var raw = IcumsHmacEnvelopeSigner.GenerateKeyMaterial();
        try
        {
            var wrapped = _signer.WrapKeyForStorage(raw);
            var row = new IcumsSigningKey
            {
                TenantId = tenantId,
                KeyId = next,
                KeyMaterialEncrypted = wrapped,
                CreatedAt = DateTimeOffset.UtcNow,
                ActivatedAt = null,
                RetiredAt = null,
                VerificationOnlyUntil = null
            };
            _db.IcumsSigningKeys.Add(row);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "IcumsGh signing key {KeyId} generated for tenant {TenantId} (inactive).",
                next, tenantId);
            return next;
        }
        finally
        {
            // Zero the raw key in memory immediately. The wrapped copy
            // is what survives; the unwrapped form should never linger.
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(raw);
        }
    }

    /// <summary>
    /// Activate <paramref name="newKeyId"/> for the given tenant and
    /// retire the previously-active key (if any), giving it a
    /// <paramref name="verificationWindow"/> overlap before its sigs
    /// stop being honored.
    /// </summary>
    /// <returns>The activation summary (id of activated key, id of retired key if any, the cutoff timestamp).</returns>
    /// <exception cref="InvalidOperationException">When the named key
    /// doesn't exist for the tenant, is already retired, or is
    /// already active.</exception>
    public async Task<KeyActivationResult> ActivateAsync(
        long tenantId,
        string newKeyId,
        TimeSpan? verificationWindow = null,
        CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(newKeyId);

        var window = verificationWindow ?? DefaultVerificationWindow;
        if (window < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(verificationWindow), "Verification window cannot be negative.");

        var now = DateTimeOffset.UtcNow;

        var newRow = await _db.IcumsSigningKeys
            .FirstOrDefaultAsync(k => k.TenantId == tenantId && k.KeyId == newKeyId, ct);
        if (newRow is null)
            throw new InvalidOperationException($"No IcumsGh signing key '{newKeyId}' for tenant {tenantId}.");
        if (newRow.RetiredAt is not null)
            throw new InvalidOperationException($"Key '{newKeyId}' is retired and cannot be activated.");
        if (newRow.ActivatedAt is not null)
            throw new InvalidOperationException($"Key '{newKeyId}' is already active.");

        // The currently-active key (if any). Multiple actives shouldn't
        // exist — a previous Activate would have retired the prior
        // one — but we defensively retire all of them in case of
        // operator error.
        var currentActive = await _db.IcumsSigningKeys
            .Where(k => k.TenantId == tenantId
                        && k.ActivatedAt != null
                        && k.RetiredAt == null
                        && k.Id != newRow.Id)
            .ToListAsync(ct);

        var verificationCutoff = now + window;
        foreach (var prior in currentActive)
        {
            prior.RetiredAt = now;
            prior.VerificationOnlyUntil = verificationCutoff;
        }
        newRow.ActivatedAt = now;

        await _db.SaveChangesAsync(ct);

        var retiredId = currentActive.Count > 0 ? currentActive[0].KeyId : null;
        _logger.LogInformation(
            "IcumsGh signing key {NewKeyId} activated for tenant {TenantId}; retired {RetiredKeyId} until {Cutoff:o}.",
            newKeyId, tenantId, retiredId ?? "(none)", verificationCutoff);

        return new KeyActivationResult(
            ActivatedKeyId: newKeyId,
            ActivatedAt: now,
            RetiredKeyId: retiredId,
            VerificationOnlyUntil: retiredId is null ? null : verificationCutoff);
    }

    /// <summary>
    /// List every signing key for the given tenant. Returns metadata
    /// only — never the wrapped or raw key material.
    /// </summary>
    public async Task<IReadOnlyList<KeySummary>> ListAsync(long tenantId, CancellationToken ct = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        return await _db.IcumsSigningKeys.AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.CreatedAt)
            .Select(k => new KeySummary(
                k.KeyId,
                k.CreatedAt,
                k.ActivatedAt,
                k.RetiredAt,
                k.VerificationOnlyUntil))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Auto-number the next key id from the existing set. Picks the
    /// max <c>k&lt;n&gt;</c> integer suffix and adds 1; ignores
    /// non-conforming key ids (someone could have inserted a key with
    /// a custom id). First key is <c>k1</c>.
    /// </summary>
    internal static string NextKeyId(IReadOnlyCollection<string> existing)
    {
        var maxN = 0;
        foreach (var id in existing)
        {
            if (string.IsNullOrEmpty(id) || id[0] != 'k') continue;
            if (int.TryParse(id.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > maxN)
                maxN = n;
        }
        return "k" + (maxN + 1).ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Outcome of <see cref="IcumsKeyRotationService.ActivateAsync"/>.</summary>
public sealed record KeyActivationResult(
    string ActivatedKeyId,
    DateTimeOffset ActivatedAt,
    string? RetiredKeyId,
    DateTimeOffset? VerificationOnlyUntil);

/// <summary>One signing-key row's metadata (no key material).</summary>
public sealed record KeySummary(
    string KeyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? RetiredAt,
    DateTimeOffset? VerificationOnlyUntil);
