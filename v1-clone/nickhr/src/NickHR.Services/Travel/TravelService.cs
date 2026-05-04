using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Travel;

public class TravelService
{
    private readonly NickHRDbContext _db;

    public TravelService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<TravelRequest> CreateAsync(TravelRequest request)
    {
        _db.Set<TravelRequest>().Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<List<TravelRequest>> GetMyRequestsAsync(int employeeId)
    {
        return await _db.Set<TravelRequest>()
            .Where(t => t.EmployeeId == employeeId)
            .Include(t => t.ApprovedBy)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<TravelRequest>> GetPendingApprovalsAsync()
    {
        return await _db.Set<TravelRequest>()
            .Where(t => t.Status == TravelRequestStatus.Pending)
            .Include(t => t.Employee)
            .OrderBy(t => t.DepartureDate)
            .ToListAsync();
    }

    public async Task ApproveAsync(int id, int approvedById)
    {
        var request = await _db.Set<TravelRequest>().FindAsync(id)
            ?? throw new KeyNotFoundException("Travel request not found.");

        request.Status = TravelRequestStatus.Approved;
        request.ApprovedById = approvedById;
        request.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task RejectAsync(int id, int approvedById)
    {
        var request = await _db.Set<TravelRequest>().FindAsync(id)
            ?? throw new KeyNotFoundException("Travel request not found.");

        request.Status = TravelRequestStatus.Rejected;
        request.ApprovedById = approvedById;
        request.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task ReconcileAsync(int id, decimal actualCost, string? notes)
    {
        var request = await _db.Set<TravelRequest>().FindAsync(id)
            ?? throw new KeyNotFoundException("Travel request not found.");

        request.Status = TravelRequestStatus.Reconciled;
        request.ActualCost = actualCost;
        request.ReconciliationNotes = notes;
        await _db.SaveChangesAsync();
    }

    public async Task<TravelRequest?> GetByIdAsync(int id)
    {
        return await _db.Set<TravelRequest>()
            .Include(t => t.Employee)
            .Include(t => t.ApprovedBy)
            .FirstOrDefaultAsync(t => t.Id == id);
    }
}
