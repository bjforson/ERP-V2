using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using NickFinance.PettyCash.Approvals;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Resolves WhatsApp approver phones via the <c>identity.user_phones</c>
/// table. Replaces the no-op shipped from <c>NickFinance.PettyCash</c>;
/// the no-op is still around so the kernel test fixtures (which don't
/// depend on Identity) keep compiling.
/// </summary>
/// <remarks>
/// We intentionally do NOT enforce <c>Verified=true</c> at v1 — the team
/// hasn't shipped a verification flow yet. When that lands, change the
/// queries to <c>WHERE verified = true</c> and remove this comment.
/// </remarks>
public sealed class IdentityApproverPhoneResolver : IApproverPhoneResolver
{
    private readonly IdentityDbContext _db;

    public IdentityApproverPhoneResolver(IdentityDbContext db)
    {
        _db = db;
    }

    public async Task<string?> ResolvePhoneByUserIdAsync(Guid userId, CancellationToken ct = default)
    {
        // Bypass the tenant query filter — the WhatsApp outbound path is
        // initiated from the kernel, which doesn't have an HTTP context.
        return await _db.UserPhones
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.Verified)   // prefer verified rows when both exist
            .ThenByDescending(p => p.CreatedAt)
            .Select(p => p.PhoneE164)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Guid?> ResolveUserIdByPhoneAsync(string phoneE164, CancellationToken ct = default)
    {
        return await _db.UserPhones
            .IgnoreQueryFilters()
            .Where(p => p.PhoneE164 == phoneE164)
            .OrderByDescending(p => p.Verified)
            .Select(p => (Guid?)p.UserId)
            .FirstOrDefaultAsync(ct);
    }
}
