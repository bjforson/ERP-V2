using Microsoft.EntityFrameworkCore;
using NickFinance.PettyCash;
using NickFinance.PettyCash.Approvals;

namespace NickFinance.WebApp.Services;

/// <summary>
/// Counts pending approval steps assigned to a given user. Used by the
/// nav badge next to the "Approvals" link so the operator sees how many
/// items are awaiting their decision without having to open the page.
/// </summary>
public interface IPendingApprovalsCounter
{
    Task<int> CountAsync(Guid userId, long tenantId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class PendingApprovalsCounter : IPendingApprovalsCounter
{
    private readonly PettyCashDbContext _db;

    public PendingApprovalsCounter(PettyCashDbContext db) => _db = db;

    public async Task<int> CountAsync(Guid userId, long tenantId, CancellationToken ct = default)
    {
        // VoucherApproval rows assigned to this user where the step is
        // still Pending. The TenantId predicate is redundant because the
        // PettyCashDbContext already applies a tenant query filter, but
        // keeping it explicit is a belt-and-braces hedge in case the
        // filter is ever bypassed (e.g. IgnoreQueryFilters in a future
        // refactor).
        return await _db.VoucherApprovals
            .Where(va => va.AssignedToUserId == userId
                      && va.Decision == ApprovalDecision.Pending
                      && va.TenantId == tenantId)
            .CountAsync(ct);
    }
}
