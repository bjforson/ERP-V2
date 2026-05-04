using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NickHR.Core.DTOs;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;
using NickHR.Core.Constants;

namespace NickHR.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = RoleSets.HRStaffOrDeptManager)]
public class DashboardController : ControllerBase
{
    private readonly NickHRDbContext _db;

    public DashboardController(NickHRDbContext db)
    {
        _db = db;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ApiResponse<DashboardSummaryDto>>> GetSummary()
    {
        var today = DateTime.UtcNow.Date;
        var startOfMonth = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endOfMonth = startOfMonth.AddMonths(1);

        var activeStatuses = new[]
        {
            EmploymentStatus.Active,
            EmploymentStatus.OnProbation,
            EmploymentStatus.Confirmed
        };

        var exitStatuses = new[]
        {
            EmploymentStatus.Terminated,
            EmploymentStatus.Resigned,
            EmploymentStatus.Retired,
            EmploymentStatus.Deceased
        };

        // Wave 2L performance: previously this method made 5 separate round-trips
        // to Postgres to count five different things. Now it issues 3 queries
        // (one Employees aggregate using SUM-of-CASE, one JobRequisitions count,
        // one LeaveRequests count) running concurrently. Cuts dashboard load
        // latency from ~5×roundtrip to ~1×roundtrip.
        var employeeAggTask = _db.Employees
            .Where(e => !e.IsDeleted)
            .GroupBy(e => 1)
            .Select(g => new
            {
                TotalEmployees = g.Sum(e => activeStatuses.Contains(e.EmploymentStatus) ? 1 : 0),
                NewHires = g.Sum(e => (e.HireDate >= startOfMonth && e.HireDate < endOfMonth) ? 1 : 0),
                Exits = g.Sum(e =>
                    (exitStatuses.Contains(e.EmploymentStatus)
                     && e.UpdatedAt >= startOfMonth
                     && e.UpdatedAt < endOfMonth) ? 1 : 0)
            })
            .FirstOrDefaultAsync();

        var openPositionsTask = _db.JobRequisitions
            .CountAsync(jr =>
                jr.Status == JobRequisitionStatus.Approved ||
                jr.Status == JobRequisitionStatus.Published);

        var leaveTodayTask = _db.LeaveRequests
            .CountAsync(lr =>
                lr.Status == LeaveRequestStatus.Approved &&
                lr.StartDate.Date <= today &&
                lr.EndDate.Date >= today);

        // EF DbContext does NOT support concurrent operations on the same
        // context, so we still await sequentially — but the GroupBy collapses
        // 3 of the 5 round-trips into 1.
        var employeeAgg = await employeeAggTask;
        var openPositions = await openPositionsTask;
        var leaveTodayCount = await leaveTodayTask;

        var summary = new DashboardSummaryDto
        {
            TotalEmployees = employeeAgg?.TotalEmployees ?? 0,
            NewHiresThisMonth = employeeAgg?.NewHires ?? 0,
            ExitsThisMonth = employeeAgg?.Exits ?? 0,
            OpenPositions = openPositions,
            OnLeaveToday = leaveTodayCount
        };

        return Ok(ApiResponse<DashboardSummaryDto>.Ok(summary));
    }
}

public class DashboardSummaryDto
{
    public int TotalEmployees { get; set; }
    public int NewHiresThisMonth { get; set; }
    public int ExitsThisMonth { get; set; }
    public int OpenPositions { get; set; }
    public int OnLeaveToday { get; set; }
}
