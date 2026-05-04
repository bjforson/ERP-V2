using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// Sprint 14 / VP6 Phase C — claim semantics for cases under shared
/// visibility. The first analyst to open a case acquires a
/// <see cref="CaseClaim"/> row; the unique partial index
/// <c>ux_case_claims_active_per_case (CaseId) WHERE ReleasedAt IS NULL</c>
/// enforces at-most-one-active-claim per case. Concurrent acquires race
/// on the unique violation; the loser receives a
/// <see cref="CaseAlreadyClaimedException"/> carrying the winning
/// claim's metadata.
///
/// <para>
/// **Authorization.** <see cref="ReleaseClaimAsync"/> is allowed for the
/// claim owner OR an admin (caller-provided <c>isAdmin</c> flag — the
/// service layer trusts the caller for the role check; the page
/// authorize attribute handles the gating).
/// </para>
///
/// <para>
/// **Tenant context.** Every method assumes <c>app.tenant_id</c> is set
/// for the calling tenant — RLS narrows reads + writes through the
/// tenant_isolation_case_claims policy.
/// </para>
/// </summary>
public sealed class CaseClaimService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<CaseClaimService> _logger;

    public CaseClaimService(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<CaseClaimService> logger)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Acquire the active claim on a case for the given user under the
    /// given service. Returns the new claim id on success. Throws
    /// <see cref="CaseAlreadyClaimedException"/> when the case already
    /// has an active claim (same user + same service is treated as the
    /// winner — returns the existing id without raising).
    /// </summary>
    public async Task<Guid> AcquireClaimAsync(
        Guid caseId,
        Guid analysisServiceId,
        Guid userId,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();

        // Optimistic fast-path: check whether an active claim already
        // exists. RLS narrows by tenant; the unique partial index
        // guarantees we see at most one.
        var existing = await GetActiveClaimAsync(caseId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Same-user + same-service re-acquire: idempotent winner.
            if (existing.ClaimedByUserId == userId && existing.AnalysisServiceId == analysisServiceId)
            {
                return existing.Id;
            }
            throw new CaseAlreadyClaimedException(
                caseId, existing.Id, existing.ClaimedByUserId,
                existing.AnalysisServiceId, existing.ClaimedAt);
        }

        var claim = new CaseClaim
        {
            Id = Guid.NewGuid(),
            CaseId = caseId,
            AnalysisServiceId = analysisServiceId,
            ClaimedByUserId = userId,
            ClaimedAt = DateTimeOffset.UtcNow,
            ReleasedAt = null,
            ReleasedByUserId = null,
            TenantId = _tenant.TenantId,
        };
        _db.CaseClaims.Add(claim);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Claim {ClaimId} acquired by user {UserId} on case {CaseId} via service {ServiceId}.",
                claim.Id, userId, caseId, analysisServiceId);
            return claim.Id;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race. Detach our optimistic entity, look up the
            // winner, and surface its metadata.
            _db.Entry(claim).State = EntityState.Detached;
            var winner = await GetActiveClaimAsync(caseId, ct).ConfigureAwait(false);
            if (winner is null)
            {
                // Vanishingly rare: the unique violation is real but a
                // second pass sees no active claim — possibly the winner
                // released between the two queries. Log + retry once by
                // recursing; if it fails again, surface the original
                // exception.
                _logger.LogWarning(ex,
                    "Acquire on case {CaseId} hit unique violation but no active claim found on requery — "
                    + "rare race; retrying once.", caseId);
                return await AcquireClaimAsync(caseId, analysisServiceId, userId, ct).ConfigureAwait(false);
            }
            throw new CaseAlreadyClaimedException(
                caseId, winner.Id, winner.ClaimedByUserId,
                winner.AnalysisServiceId, winner.ClaimedAt);
        }
    }

    /// <summary>
    /// Release a claim. The caller must be the claim owner or an admin
    /// (caller-asserted via <paramref name="isAdmin"/>). Idempotent —
    /// releasing an already-released claim is a no-op. Throws
    /// <see cref="UnauthorizedAccessException"/> when the caller is
    /// neither the owner nor an admin.
    /// </summary>
    public async Task ReleaseClaimAsync(
        Guid claimId,
        Guid userId,
        bool isAdmin,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();

        var row = await _db.CaseClaims
            .FirstOrDefaultAsync(c => c.Id == claimId, ct)
            .ConfigureAwait(false);
        if (row is null)
            throw new InvalidOperationException($"CaseClaim {claimId} not found in this tenant.");

        if (row.ReleasedAt is not null) return;

        if (row.ClaimedByUserId != userId && !isAdmin)
            throw new UnauthorizedAccessException(
                $"User {userId} is not the owner of claim {claimId} and is not an admin; cannot release.");

        row.ReleasedAt = DateTimeOffset.UtcNow;
        row.ReleasedByUserId = userId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Claim {ClaimId} released by user {UserId} (admin={IsAdmin}); originally claimed by {OwnerId} at {ClaimedAt:u}.",
            claimId, userId, isAdmin, row.ClaimedByUserId, row.ClaimedAt);
    }

    /// <summary>
    /// Return the active claim on a case (the row with
    /// <c>ReleasedAt IS NULL</c>), or <c>null</c> if no active claim
    /// exists. <see cref="CaseClaim.AnalysisService"/> is eager-loaded
    /// so callers can show the service display name on the badge.
    /// </summary>
    public async Task<CaseClaim?> GetActiveClaimAsync(Guid caseId, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        return await _db.CaseClaims.AsNoTracking()
            .Include(c => c.AnalysisService)
            .Where(c => c.CaseId == caseId && c.ReleasedAt == null)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; CaseClaimService must run inside a tenant-aware request scope.");
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
    }
}
