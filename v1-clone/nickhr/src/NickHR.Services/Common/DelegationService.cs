using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.System;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Common;

public class DelegationService
{
    private readonly NickHRDbContext _db;

    public DelegationService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<ApprovalDelegation> CreateAsync(ApprovalDelegation delegation)
    {
        // Validate no overlapping active delegation
        var overlap = await _db.Set<ApprovalDelegation>()
            .AnyAsync(d => d.DelegatorId == delegation.DelegatorId
                && d.IsActive
                && d.StartDate <= delegation.EndDate
                && d.EndDate >= delegation.StartDate);

        if (overlap)
            throw new InvalidOperationException("An overlapping delegation already exists.");

        _db.Set<ApprovalDelegation>().Add(delegation);
        await _db.SaveChangesAsync();
        return delegation;
    }

    public async Task<List<ApprovalDelegation>> GetActiveAsync()
    {
        return await _db.Set<ApprovalDelegation>()
            .Where(d => d.IsActive && d.EndDate >= DateTime.UtcNow)
            .Include(d => d.Delegator)
            .Include(d => d.Delegate)
            .OrderBy(d => d.StartDate)
            .ToListAsync();
    }

    public async Task<List<ApprovalDelegation>> GetMyDelegationsAsync(int employeeId)
    {
        return await _db.Set<ApprovalDelegation>()
            .Where(d => d.DelegatorId == employeeId || d.DelegateId == employeeId)
            .Include(d => d.Delegator)
            .Include(d => d.Delegate)
            .OrderByDescending(d => d.StartDate)
            .ToListAsync();
    }

    public async Task DeactivateAsync(int id)
    {
        var delegation = await _db.Set<ApprovalDelegation>().FindAsync(id)
            ?? throw new KeyNotFoundException("Delegation not found.");

        delegation.IsActive = false;
        await _db.SaveChangesAsync();
    }
}
