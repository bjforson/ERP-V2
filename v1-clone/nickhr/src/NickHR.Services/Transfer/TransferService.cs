using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Core;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Transfer;

public interface ITransferService
{
    Task<TransferPromotion> InitiateAsync(
        int employeeId, TransferType type, DateTime effectiveDate,
        int toDepartmentId, int toDesignationId, int toGradeId,
        int? toLocationId, decimal newSalary, string reason);
    Task<TransferPromotion> ApproveAsync(int id, int approverId);
    Task<TransferPromotion> RejectAsync(int id, int approverId, string reason);
    Task<List<TransferPromotion>> GetPendingAsync();
    Task<List<TransferPromotion>> GetEmployeeHistoryAsync(int employeeId);
}

public class TransferService : ITransferService
{
    private readonly NickHRDbContext _db;

    public TransferService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<TransferPromotion> InitiateAsync(
        int employeeId, TransferType type, DateTime effectiveDate,
        int toDepartmentId, int toDesignationId, int toGradeId,
        int? toLocationId, decimal newSalary, string reason)
    {
        var employee = await _db.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .Include(e => e.Grade)
            .Include(e => e.Location)
            .FirstOrDefaultAsync(e => e.Id == employeeId && !e.IsDeleted)
            ?? throw new KeyNotFoundException($"Employee {employeeId} not found.");

        // Validate target entities exist
        _ = await _db.Departments.FindAsync(toDepartmentId)
            ?? throw new KeyNotFoundException($"Department {toDepartmentId} not found.");
        _ = await _db.Designations.FindAsync(toDesignationId)
            ?? throw new KeyNotFoundException($"Designation {toDesignationId} not found.");
        _ = await _db.Grades.FindAsync(toGradeId)
            ?? throw new KeyNotFoundException($"Grade {toGradeId} not found.");

        if (toLocationId.HasValue)
        {
            _ = await _db.Locations.FindAsync(toLocationId.Value)
                ?? throw new KeyNotFoundException($"Location {toLocationId} not found.");
        }

        // Check no pending transfer exists
        var hasPending = await _db.TransferPromotions.AnyAsync(t =>
            t.EmployeeId == employeeId
            && t.Status == ApprovalStatus.Pending
            && !t.IsDeleted);

        if (hasPending)
            throw new InvalidOperationException("A pending transfer/promotion already exists for this employee.");

        var transfer = new TransferPromotion
        {
            EmployeeId = employeeId,
            Type = type,
            EffectiveDate = effectiveDate.Date,
            Reason = reason,
            // From fields - auto-filled from current employee state
            FromDepartmentId = employee.DepartmentId ?? toDepartmentId,
            FromDesignationId = employee.DesignationId ?? toDesignationId,
            FromGradeId = employee.GradeId ?? toGradeId,
            FromLocationId = employee.LocationId,
            OldBasicSalary = employee.BasicSalary,
            // To fields
            ToDepartmentId = toDepartmentId,
            ToDesignationId = toDesignationId,
            ToGradeId = toGradeId,
            ToLocationId = toLocationId,
            NewBasicSalary = newSalary,
            Status = ApprovalStatus.Pending
        };

        _db.TransferPromotions.Add(transfer);
        await _db.SaveChangesAsync();
        return transfer;
    }

    public async Task<TransferPromotion> ApproveAsync(int id, int approverId)
    {
        var transfer = await _db.TransferPromotions
            .Include(t => t.Employee)
            .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted)
            ?? throw new KeyNotFoundException($"Transfer/Promotion {id} not found.");

        if (transfer.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException("Only pending transfers can be approved.");

        transfer.Status = ApprovalStatus.Approved;
        transfer.ApprovedById = approverId;
        transfer.ApprovedAt = DateTime.UtcNow;

        // Apply changes to employee
        var employee = transfer.Employee;
        employee.DepartmentId = transfer.ToDepartmentId;
        employee.DesignationId = transfer.ToDesignationId;
        employee.GradeId = transfer.ToGradeId;
        employee.LocationId = transfer.ToLocationId;
        employee.BasicSalary = transfer.NewBasicSalary;

        await _db.SaveChangesAsync();
        return transfer;
    }

    public async Task<TransferPromotion> RejectAsync(int id, int approverId, string reason)
    {
        var transfer = await _db.TransferPromotions.FindAsync(id)
            ?? throw new KeyNotFoundException($"Transfer/Promotion {id} not found.");

        if (transfer.Status != ApprovalStatus.Pending)
            throw new InvalidOperationException("Only pending transfers can be rejected.");

        transfer.Status = ApprovalStatus.Rejected;
        transfer.ApprovedById = approverId;
        transfer.ApprovedAt = DateTime.UtcNow;
        transfer.RejectionReason = reason;

        await _db.SaveChangesAsync();
        return transfer;
    }

    public async Task<List<TransferPromotion>> GetPendingAsync()
    {
        return await _db.TransferPromotions
            .Include(t => t.Employee)
                .ThenInclude(e => e.Department)
            .Include(t => t.ToDepartment)
            .Include(t => t.ToDesignation)
            .Where(t => t.Status == ApprovalStatus.Pending && !t.IsDeleted)
            .OrderBy(t => t.EffectiveDate)
            .ToListAsync();
    }

    public async Task<List<TransferPromotion>> GetEmployeeHistoryAsync(int employeeId)
    {
        return await _db.TransferPromotions
            .Include(t => t.FromDepartment)
            .Include(t => t.ToDepartment)
            .Include(t => t.FromDesignation)
            .Include(t => t.ToDesignation)
            .Include(t => t.ApprovedBy)
            .Where(t => t.EmployeeId == employeeId && !t.IsDeleted)
            .OrderByDescending(t => t.EffectiveDate)
            .ToListAsync();
    }
}
