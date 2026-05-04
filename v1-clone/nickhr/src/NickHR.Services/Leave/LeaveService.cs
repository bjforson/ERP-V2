using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs;
using NickHR.Core.DTOs.Leave;
using NickHR.Core.Entities.Leave;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Leave;

public class LeaveService : ILeaveService
{
    private readonly NickHRDbContext _db;
    private readonly IUnitOfWork _unitOfWork;

    public LeaveService(NickHRDbContext db, IUnitOfWork unitOfWork)
    {
        _db = db;
        _unitOfWork = unitOfWork;
    }

    public async Task<LeaveRequestDto> RequestLeaveAsync(int employeeId, CreateLeaveRequestDto dto)
    {
        if (dto.EndDate < dto.StartDate)
            throw new ArgumentException("End date cannot be before start date.");

        var policy = await _db.LeavePolicies.FirstOrDefaultAsync(p => p.Id == dto.LeavePolicyId && p.IsActive && !p.IsDeleted)
            ?? throw new KeyNotFoundException($"Leave policy with ID {dto.LeavePolicyId} not found or inactive.");

        var numberOfDays = CalculateWorkingDays(dto.StartDate, dto.EndDate);

        // Check balance for the current year
        var year = dto.StartDate.Year;
        var balance = await _db.LeaveBalances
            .FirstOrDefaultAsync(b => b.EmployeeId == employeeId &&
                                      b.LeavePolicyId == dto.LeavePolicyId &&
                                      b.Year == year &&
                                      !b.IsDeleted);

        if (balance is not null && balance.Available < numberOfDays)
            throw new InvalidOperationException(
                $"Insufficient leave balance. Available: {balance.Available} day(s), Requested: {numberOfDays} day(s).");

        // Check for conflicting approved/pending requests
        var conflict = await _db.LeaveRequests.AnyAsync(r =>
            r.EmployeeId == employeeId &&
            !r.IsDeleted &&
            (r.Status == LeaveRequestStatus.Pending || r.Status == LeaveRequestStatus.Approved) &&
            r.StartDate <= dto.EndDate && r.EndDate >= dto.StartDate);

        if (conflict)
            throw new InvalidOperationException("A leave request already exists for the selected date range.");

        var request = new LeaveRequest
        {
            EmployeeId = employeeId,
            LeavePolicyId = dto.LeavePolicyId,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            NumberOfDays = numberOfDays,
            Reason = dto.Reason,
            MedicalCertificatePath = dto.MedicalCertificatePath,
            HandoverNotes = dto.HandoverNotes,
            Status = LeaveRequestStatus.Pending
        };

        _db.LeaveRequests.Add(request);

        // Increment pending balance
        if (balance is not null)
        {
            balance.Pending += numberOfDays;
        }

        await _unitOfWork.SaveChangesAsync();

        return await MapToRequestDtoAsync(request.Id);
    }

    public async Task<LeaveRequestDto> ApproveLeaveAsync(int requestId, int approverId)
    {
        var request = await _db.LeaveRequests
            .Include(r => r.Employee)
            .FirstOrDefaultAsync(r => r.Id == requestId && !r.IsDeleted)
            ?? throw new KeyNotFoundException($"Leave request with ID {requestId} not found.");

        if (request.Status != LeaveRequestStatus.Pending)
            throw new InvalidOperationException($"Leave request is already {request.Status}. Only Pending requests can be approved.");

        request.Status = LeaveRequestStatus.Approved;
        request.ApprovedById = approverId;
        request.ApprovedAt = DateTime.UtcNow;

        // Move from Pending to Taken in the balance
        var year = request.StartDate.Year;
        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == request.EmployeeId &&
            b.LeavePolicyId == request.LeavePolicyId &&
            b.Year == year &&
            !b.IsDeleted);

        if (balance is not null)
        {
            balance.Pending = Math.Max(0, balance.Pending - request.NumberOfDays);
            balance.Taken += request.NumberOfDays;
        }

        await _unitOfWork.SaveChangesAsync();

        return await MapToRequestDtoAsync(requestId);
    }

    public async Task<LeaveRequestDto> RejectLeaveAsync(int requestId, int approverId, string reason)
    {
        var request = await _db.LeaveRequests
            .Include(r => r.Employee)
            .FirstOrDefaultAsync(r => r.Id == requestId && !r.IsDeleted)
            ?? throw new KeyNotFoundException($"Leave request with ID {requestId} not found.");

        if (request.Status != LeaveRequestStatus.Pending)
            throw new InvalidOperationException($"Leave request is already {request.Status}. Only Pending requests can be rejected.");

        request.Status = LeaveRequestStatus.Rejected;
        request.ApprovedById = approverId;
        request.ApprovedAt = DateTime.UtcNow;
        request.RejectionReason = reason;

        // Release the pending balance
        var year = request.StartDate.Year;
        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == request.EmployeeId &&
            b.LeavePolicyId == request.LeavePolicyId &&
            b.Year == year &&
            !b.IsDeleted);

        if (balance is not null)
            balance.Pending = Math.Max(0, balance.Pending - request.NumberOfDays);

        await _unitOfWork.SaveChangesAsync();

        return await MapToRequestDtoAsync(requestId);
    }

    public async Task<List<LeaveBalanceDto>> GetBalancesAsync(int employeeId)
    {
        var year = DateTime.UtcNow.Year;

        var balances = await _db.LeaveBalances
            .Include(b => b.LeavePolicy)
            .Where(b => b.EmployeeId == employeeId && b.Year == year && !b.IsDeleted)
            .OrderBy(b => b.LeavePolicy.LeaveType)
            .ToListAsync();

        return balances.Select(b => new LeaveBalanceDto
        {
            LeaveType = b.LeavePolicy.LeaveType,
            Entitled = b.Entitled,
            Taken = b.Taken,
            Pending = b.Pending,
            CarriedForward = b.CarriedForward,
            Available = b.Available
        }).ToList();
    }

    /// <summary>Original overload - required by interface; delegates to the nullable-departmentId version.</summary>
    public Task<List<TeamLeaveCalendarDto>> GetTeamCalendarAsync(int departmentId, DateTime start, DateTime end)
        => GetTeamCalendarAsync((int?)departmentId, start, end);

    // -------------------------------------------------------------------------
    // Enhanced methods
    // -------------------------------------------------------------------------

    public async Task<PagedResult<LeaveRequestDto>> GetAllRequestsAsync(
        int? employeeId,
        LeaveRequestStatus? status,
        int page,
        int pageSize)
    {
        var query = _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeavePolicy)
            .Include(r => r.ApprovedBy)
            .Where(r => !r.IsDeleted);

        if (employeeId.HasValue)
            query = query.Where(r => r.EmployeeId == employeeId.Value);

        if (status.HasValue)
            query = query.Where(r => r.Status == status.Value);

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return new PagedResult<LeaveRequestDto>
        {
            Items = items.Select(MapToDto).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<List<LeaveRequestDto>> GetPendingApprovalsAsync(int? departmentId)
    {
        var query = _db.LeaveRequests
            .Include(r => r.Employee)
                .ThenInclude(e => e.Department)
            .Include(r => r.LeavePolicy)
            .Include(r => r.ApprovedBy)
            .Where(r => !r.IsDeleted && r.Status == LeaveRequestStatus.Pending);

        if (departmentId.HasValue)
            query = query.Where(r => r.Employee.DepartmentId == departmentId.Value);

        var items = await query
            .OrderBy(r => r.StartDate)
            .ToListAsync();

        return items.Select(MapToDto).ToList();
    }

    public async Task<LeaveRequestDto> CancelLeaveAsync(int requestId, int employeeId)
    {
        var request = await _db.LeaveRequests
            .Include(r => r.Employee)
            .FirstOrDefaultAsync(r => r.Id == requestId && !r.IsDeleted)
            ?? throw new KeyNotFoundException($"Leave request with ID {requestId} not found.");

        if (request.EmployeeId != employeeId)
            throw new UnauthorizedAccessException("You can only cancel your own leave requests.");

        if (request.Status != LeaveRequestStatus.Pending)
            throw new InvalidOperationException($"Only Pending requests can be cancelled. Current status: {request.Status}.");

        request.Status = LeaveRequestStatus.Cancelled;

        // Restore the pending days back to available
        var year = request.StartDate.Year;
        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeId == request.EmployeeId &&
            b.LeavePolicyId == request.LeavePolicyId &&
            b.Year == year &&
            !b.IsDeleted);

        if (balance is not null)
            balance.Pending = Math.Max(0, balance.Pending - request.NumberOfDays);

        await _unitOfWork.SaveChangesAsync();

        return await MapToRequestDtoAsync(requestId);
    }

    public async Task<List<TeamLeaveCalendarDto>> GetTeamCalendarAsync(int? departmentId, DateTime startDate, DateTime endDate)
    {
        var query = _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeavePolicy)
            .Where(r =>
                !r.IsDeleted &&
                !r.Employee.IsDeleted &&
                (r.Status == LeaveRequestStatus.Approved || r.Status == LeaveRequestStatus.Pending) &&
                r.StartDate <= endDate && r.EndDate >= startDate);

        if (departmentId.HasValue)
            query = query.Where(r => r.Employee.DepartmentId == departmentId.Value);

        var requests = await query.OrderBy(r => r.StartDate).ToListAsync();

        return requests.Select(r => new TeamLeaveCalendarDto
        {
            EmployeeName = $"{r.Employee.FirstName} {r.Employee.LastName}".Trim(),
            LeaveType = r.LeavePolicy.LeaveType,
            StartDate = r.StartDate,
            EndDate = r.EndDate,
            Status = r.Status
        }).ToList();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static decimal CalculateWorkingDays(DateTime start, DateTime end)
    {
        decimal days = 0;
        var current = start.Date;
        while (current <= end.Date)
        {
            if (current.DayOfWeek != DayOfWeek.Saturday && current.DayOfWeek != DayOfWeek.Sunday)
                days++;
            current = current.AddDays(1);
        }
        return days;
    }

    private static LeaveRequestDto MapToDto(LeaveRequest request) => new()
    {
        Id = request.Id,
        EmployeeName = $"{request.Employee.FirstName} {request.Employee.LastName}".Trim(),
        LeaveType = request.LeavePolicy.LeaveType,
        StartDate = request.StartDate,
        EndDate = request.EndDate,
        NumberOfDays = request.NumberOfDays,
        Reason = request.Reason,
        Status = request.Status,
        ApprovedBy = request.ApprovedBy is not null
            ? $"{request.ApprovedBy.FirstName} {request.ApprovedBy.LastName}".Trim()
            : null,
        ApprovedAt = request.ApprovedAt
    };

    private async Task<LeaveRequestDto> MapToRequestDtoAsync(int requestId)
    {
        var request = await _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeavePolicy)
            .Include(r => r.ApprovedBy)
            .FirstOrDefaultAsync(r => r.Id == requestId)
            ?? throw new InvalidOperationException($"Leave request {requestId} could not be retrieved.");

        return MapToDto(request);
    }
}
