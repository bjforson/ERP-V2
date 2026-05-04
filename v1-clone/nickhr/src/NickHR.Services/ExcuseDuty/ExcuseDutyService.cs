using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Leave;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.ExcuseDuty;

public class ExcuseDutyService : IExcuseDutyService
{
    private readonly NickHRDbContext _db;

    // Maximum excuse duty hours per calendar month per employee
    private const decimal MaxMonthlyHours = 8m;

    public ExcuseDutyService(NickHRDbContext db)
    {
        _db = db;
    }

    // -------------------------------------------------------------------------
    // Request
    // -------------------------------------------------------------------------
    public async Task<ExcuseDutyDto> RequestExcuseDutyAsync(
        int employeeId,
        ExcuseDutyType type,
        DateTime date,
        TimeSpan startTime,
        TimeSpan endTime,
        string reason,
        string? destination = null,
        string? medicalCertPath = null)
    {
        if (endTime <= startTime)
            throw new ArgumentException("End time must be after start time.");

        var durationHours = (decimal)(endTime - startTime).TotalHours;

        if (durationHours <= 0)
            throw new ArgumentException("Duration must be positive.");

        // Monthly hours check
        var monthlyUsed = await _db.ExcuseDuties
            .Where(e => e.EmployeeId == employeeId
                        && !e.IsDeleted
                        && e.Date.Year == date.Year
                        && e.Date.Month == date.Month
                        && (e.Status == ExcuseDutyStatus.Pending || e.Status == ExcuseDutyStatus.Approved))
            .SumAsync(e => (decimal?)e.DurationHours) ?? 0m;

        if (monthlyUsed + durationHours > MaxMonthlyHours)
            throw new InvalidOperationException(
                $"Exceeds monthly excuse duty limit of {MaxMonthlyHours}h. Used: {monthlyUsed}h, Requested: {durationHours}h.");

        if (type == ExcuseDutyType.Medical && string.IsNullOrWhiteSpace(medicalCertPath))
            throw new InvalidOperationException("Medical certificate path is required for Medical type excuse duty.");

        var entity = new NickHR.Core.Entities.Leave.ExcuseDuty
        {
            EmployeeId = employeeId,
            ExcuseDutyType = type,
            Date = date.Date,
            StartTime = startTime,
            EndTime = endTime,
            DurationHours = Math.Round(durationHours, 2),
            Reason = reason,
            Destination = destination,
            MedicalCertificatePath = medicalCertPath,
            Status = ExcuseDutyStatus.Pending
        };

        _db.ExcuseDuties.Add(entity);
        await _db.SaveChangesAsync();

        return await ProjectAsync(entity.Id);
    }

    // -------------------------------------------------------------------------
    // Queries
    // -------------------------------------------------------------------------
    public async Task<List<ExcuseDutyDto>> GetMyRequestsAsync(int employeeId, int? month, int? year)
    {
        var query = _db.ExcuseDuties.Where(e => e.EmployeeId == employeeId && !e.IsDeleted);

        if (year.HasValue)
            query = query.Where(e => e.Date.Year == year.Value);
        if (month.HasValue)
            query = query.Where(e => e.Date.Month == month.Value);

        var ids = await query.OrderByDescending(e => e.Date).Select(e => e.Id).ToListAsync();

        var result = new List<ExcuseDutyDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    public async Task<List<ExcuseDutyDto>> GetPendingApprovalsAsync()
    {
        var ids = await _db.ExcuseDuties
            .Where(e => !e.IsDeleted && e.Status == ExcuseDutyStatus.Pending)
            .OrderByDescending(e => e.Date)
            .Select(e => e.Id)
            .ToListAsync();

        var result = new List<ExcuseDutyDto>();
        foreach (var id in ids)
            result.Add(await ProjectAsync(id));
        return result;
    }

    // -------------------------------------------------------------------------
    // Approve / Reject / Confirm Return
    // -------------------------------------------------------------------------
    public async Task<ExcuseDutyDto> ApproveAsync(int id, int approverId)
    {
        var entity = await GetEntityAsync(id);
        if (entity.Status != ExcuseDutyStatus.Pending)
            throw new InvalidOperationException($"Excuse duty is not Pending (current: {entity.Status}).");

        entity.Status = ExcuseDutyStatus.Approved;
        entity.ApprovedById = approverId;
        entity.ApprovedAt = DateTime.UtcNow;

        // Update AttendanceRecord for that day to HalfDay
        var attendance = await _db.AttendanceRecords
            .FirstOrDefaultAsync(a => a.EmployeeId == entity.EmployeeId && a.Date == entity.Date && !a.IsDeleted);

        if (attendance is not null)
        {
            attendance.AttendanceType = AttendanceType.HalfDay;
        }

        await _db.SaveChangesAsync();
        return await ProjectAsync(id);
    }

    public async Task<ExcuseDutyDto> RejectAsync(int id, int approverId, string reason)
    {
        var entity = await GetEntityAsync(id);
        if (entity.Status != ExcuseDutyStatus.Pending)
            throw new InvalidOperationException($"Excuse duty is not Pending (current: {entity.Status}).");

        entity.Status = ExcuseDutyStatus.Rejected;
        entity.ApprovedById = approverId;
        entity.ApprovedAt = DateTime.UtcNow;
        entity.RejectionReason = reason;
        await _db.SaveChangesAsync();

        return await ProjectAsync(id);
    }

    public async Task<ExcuseDutyDto> ConfirmReturnAsync(int id, TimeSpan returnTime)
    {
        var entity = await GetEntityAsync(id);
        if (entity.Status != ExcuseDutyStatus.Approved)
            throw new InvalidOperationException($"Excuse duty must be Approved to confirm return (current: {entity.Status}).");

        entity.ReturnConfirmed = true;
        entity.ReturnTime = returnTime;
        entity.ActualDurationHours = returnTime > entity.StartTime
            ? Math.Round((decimal)(returnTime - entity.StartTime).TotalHours, 2)
            : entity.DurationHours;
        await _db.SaveChangesAsync();

        return await ProjectAsync(id);
    }

    // -------------------------------------------------------------------------
    // Monthly Report
    // -------------------------------------------------------------------------
    public async Task<List<ExcuseDutyMonthlyReportDto>> GetMonthlyReportAsync(int? departmentId, int month, int year)
    {
        var query = _db.ExcuseDuties
            .Include(e => e.Employee)
            .ThenInclude(emp => emp.Department)
            .Where(e => !e.IsDeleted && e.Date.Year == year && e.Date.Month == month);

        if (departmentId.HasValue)
            query = query.Where(e => e.Employee.DepartmentId == departmentId.Value);

        var records = await query.ToListAsync();

        return records
            .GroupBy(e => e.EmployeeId)
            .Select(g =>
            {
                var first = g.First();
                var hoursByType = g
                    .GroupBy(x => x.ExcuseDutyType.ToString())
                    .ToDictionary(t => t.Key, t => t.Sum(x => x.DurationHours));

                return new ExcuseDutyMonthlyReportDto(
                    g.Key,
                    $"{first.Employee.FirstName} {first.Employee.LastName}",
                    first.Employee.DepartmentId,
                    first.Employee.Department?.Name,
                    month,
                    year,
                    g.Sum(x => x.DurationHours),
                    g.Count(),
                    hoursByType
                );
            })
            .OrderBy(r => r.EmployeeName)
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------
    private async Task<NickHR.Core.Entities.Leave.ExcuseDuty> GetEntityAsync(int id)
    {
        return await _db.ExcuseDuties.FirstOrDefaultAsync(e => e.Id == id && !e.IsDeleted)
            ?? throw new KeyNotFoundException($"Excuse duty {id} not found.");
    }

    private async Task<ExcuseDutyDto> ProjectAsync(int id)
    {
        var e = await _db.ExcuseDuties
            .Include(x => x.Employee).ThenInclude(emp => emp.Department)
            .Include(x => x.ApprovedBy)
            .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted)
            ?? throw new KeyNotFoundException($"Excuse duty {id} not found.");

        return new ExcuseDutyDto(
            e.Id,
            e.EmployeeId,
            $"{e.Employee.FirstName} {e.Employee.LastName}",
            e.Employee.DepartmentId,
            e.Employee.Department?.Name,
            e.ExcuseDutyType,
            e.Date,
            e.StartTime,
            e.EndTime,
            e.DurationHours,
            e.Reason,
            e.Destination,
            e.Status,
            e.ApprovedById,
            e.ApprovedBy is null ? null : $"{e.ApprovedBy.FirstName} {e.ApprovedBy.LastName}",
            e.ApprovedAt,
            e.RejectionReason,
            e.MedicalCertificatePath,
            e.ReturnConfirmed,
            e.ReturnTime,
            e.ActualDurationHours,
            e.CreatedAt
        );
    }
}
