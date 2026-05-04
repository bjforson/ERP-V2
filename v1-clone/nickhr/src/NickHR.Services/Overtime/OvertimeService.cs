using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Leave;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Overtime;

public interface IOvertimeService
{
    Task<OvertimeRequest> RequestOvertimeAsync(int employeeId, DateTime date, TimeSpan startTime, TimeSpan endTime, string reason);
    Task<List<OvertimeRequest>> GetMyRequestsAsync(int employeeId, int? month = null, int? year = null);
    Task<List<OvertimeRequest>> GetPendingApprovalsAsync();
    Task<OvertimeRequest> ApproveAsync(int id, int approverId);
    Task<OvertimeRequest> RejectAsync(int id, int approverId, string reason);
    Task<OvertimeRequest> CompleteAsync(int id, decimal actualHours);
    Task<List<OvertimeRequest>> GetOvertimeForPayrollAsync(int employeeId, int month, int year);
}

public class OvertimeService : IOvertimeService
{
    private readonly NickHRDbContext _db;

    public OvertimeService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<OvertimeRequest> RequestOvertimeAsync(
        int employeeId, DateTime date, TimeSpan startTime, TimeSpan endTime, string reason)
    {
        var employee = await _db.Employees.FindAsync(employeeId)
            ?? throw new KeyNotFoundException($"Employee {employeeId} not found.");

        if (endTime <= startTime)
            throw new ArgumentException("End time must be after start time.");

        var plannedHours = (decimal)(endTime - startTime).TotalHours;

        // Determine rate: 2.0 for weekend/public holiday, 1.5 for weekday
        var rate = IsWeekend(date) ? 2.0m : 1.5m;

        // Check if date is a holiday
        var isHoliday = await _db.Holidays.AnyAsync(h =>
            h.Date.Date == date.Date && !h.IsDeleted);
        if (isHoliday) rate = 2.0m;

        var request = new OvertimeRequest
        {
            EmployeeId = employeeId,
            Date = date.Date,
            StartTime = startTime,
            EndTime = endTime,
            PlannedHours = plannedHours,
            Reason = reason,
            Status = OvertimeRequestStatus.Pending,
            Rate = rate
        };

        _db.OvertimeRequests.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<List<OvertimeRequest>> GetMyRequestsAsync(int employeeId, int? month = null, int? year = null)
    {
        var query = _db.OvertimeRequests
            .Include(o => o.ApprovedBy)
            .Where(o => o.EmployeeId == employeeId && !o.IsDeleted);

        if (month.HasValue)
            query = query.Where(o => o.Date.Month == month.Value);
        if (year.HasValue)
            query = query.Where(o => o.Date.Year == year.Value);

        return await query.OrderByDescending(o => o.Date).ToListAsync();
    }

    public async Task<List<OvertimeRequest>> GetPendingApprovalsAsync()
    {
        return await _db.OvertimeRequests
            .Include(o => o.Employee)
            .Where(o => o.Status == OvertimeRequestStatus.Pending && !o.IsDeleted)
            .OrderBy(o => o.Date)
            .ToListAsync();
    }

    public async Task<OvertimeRequest> ApproveAsync(int id, int approverId)
    {
        var request = await _db.OvertimeRequests.FindAsync(id)
            ?? throw new KeyNotFoundException($"Overtime request {id} not found.");

        if (request.Status != OvertimeRequestStatus.Pending)
            throw new InvalidOperationException("Only pending requests can be approved.");

        request.Status = OvertimeRequestStatus.Approved;
        request.ApprovedById = approverId;
        request.ApprovedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<OvertimeRequest> RejectAsync(int id, int approverId, string reason)
    {
        var request = await _db.OvertimeRequests.FindAsync(id)
            ?? throw new KeyNotFoundException($"Overtime request {id} not found.");

        if (request.Status != OvertimeRequestStatus.Pending)
            throw new InvalidOperationException("Only pending requests can be rejected.");

        request.Status = OvertimeRequestStatus.Rejected;
        request.ApprovedById = approverId;
        request.ApprovedAt = DateTime.UtcNow;
        request.RejectionReason = reason;

        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<OvertimeRequest> CompleteAsync(int id, decimal actualHours)
    {
        var request = await _db.OvertimeRequests
            .Include(o => o.Employee)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw new KeyNotFoundException($"Overtime request {id} not found.");

        if (request.Status != OvertimeRequestStatus.Approved)
            throw new InvalidOperationException("Only approved requests can be completed.");

        if (actualHours <= 0)
            throw new ArgumentException("Actual hours must be positive.");

        request.ActualHours = actualHours;
        request.Status = OvertimeRequestStatus.Completed;

        // Calculate pay: actualHours * basicSalary/monthlyHours * rate
        // Using standard 176 working hours per month
        var hourlyRate = request.Employee.BasicSalary / 176m;
        request.PayAmount = actualHours * hourlyRate * request.Rate;

        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<List<OvertimeRequest>> GetOvertimeForPayrollAsync(int employeeId, int month, int year)
    {
        return await _db.OvertimeRequests
            .Where(o => o.EmployeeId == employeeId
                && o.Date.Month == month
                && o.Date.Year == year
                && (o.Status == OvertimeRequestStatus.Approved || o.Status == OvertimeRequestStatus.Completed)
                && !o.IsDeleted)
            .ToListAsync();
    }

    private static bool IsWeekend(DateTime date)
        => date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
}
