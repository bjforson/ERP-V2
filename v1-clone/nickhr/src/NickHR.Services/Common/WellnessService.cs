using Microsoft.EntityFrameworkCore;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Common;

public class WellnessService
{
    private readonly NickHRDbContext _db;

    public WellnessService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<WellnessMetricsDto> GetWellnessMetricsAsync()
    {
        var now = DateTime.UtcNow;
        var sixMonthsAgo = now.AddMonths(-6);
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endOfMonth = startOfMonth.AddMonths(1);

        var activeStatuses = new[] { EmploymentStatus.Active, EmploymentStatus.Confirmed, EmploymentStatus.OnProbation };

        var activeEmployeeIds = await _db.Employees
            .Where(e => activeStatuses.Contains(e.EmploymentStatus))
            .Select(e => e.Id)
            .ToListAsync();

        var totalActive = activeEmployeeIds.Count;

        // Overtime: approved requests in current month
        var overtimeThisMonth = await _db.OvertimeRequests
            .Where(o => activeEmployeeIds.Contains(o.EmployeeId)
                && o.Status == OvertimeRequestStatus.Approved
                && o.Date >= startOfMonth && o.Date < endOfMonth)
            .GroupBy(o => o.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, TotalHours = g.Sum(o => o.ActualHours ?? o.PlannedHours) })
            .ToListAsync();

        var avgOvertimePerMonth = totalActive > 0
            ? (double)overtimeThisMonth.Sum(o => (double)o.TotalHours) / totalActive
            : 0;

        var employeesWithExcessiveOvertime = overtimeThisMonth.Count(o => o.TotalHours > 20);

        // Leave utilization for current year
        var currentYear = now.Year;
        var leaveBalances = await _db.LeaveBalances
            .Where(l => l.Year == currentYear && activeEmployeeIds.Contains(l.EmployeeId))
            .ToListAsync();

        var totalEntitled = leaveBalances.Sum(l => (double)l.Entitled);
        var totalTaken = leaveBalances.Sum(l => (double)l.Taken);
        var leaveUtilizationPercent = totalEntitled > 0 ? (totalTaken / totalEntitled) * 100.0 : 0;

        // Employees who took no leave in 6 months
        var employeesWithLeaveInPeriod = await _db.LeaveRequests
            .Where(r => r.Status == LeaveRequestStatus.Approved
                && r.StartDate >= sixMonthsAgo
                && activeEmployeeIds.Contains(r.EmployeeId))
            .Select(r => r.EmployeeId)
            .Distinct()
            .ToListAsync();

        var employeesWithNoLeaveTaken = totalActive - employeesWithLeaveInPeriod.Count;

        // Absenteeism: unapproved absences (absent attendance records)
        var absentCount = await _db.AttendanceRecords
            .Where(a => a.Date >= startOfMonth && a.Date < endOfMonth
                && a.AttendanceType == AttendanceType.Absent
                && activeEmployeeIds.Contains(a.EmployeeId))
            .CountAsync();

        var workingDaysInMonth = 22; // approximation
        var totalExpectedAttendance = totalActive * workingDaysInMonth;
        var absenteeismRate = totalExpectedAttendance > 0
            ? (double)absentCount / totalExpectedAttendance * 100.0
            : 0;

        // Burnout risk: high overtime (>15h this month) AND no leave in 6 months
        var highOvertimeEmployeeIds = overtimeThisMonth
            .Where(o => o.TotalHours > 15)
            .Select(o => o.EmployeeId)
            .ToHashSet();

        var employeesWithNoLeaveHashSet = activeEmployeeIds
            .Except(employeesWithLeaveInPeriod)
            .ToHashSet();

        var employeesAtBurnoutRisk = highOvertimeEmployeeIds.Count(id => employeesWithNoLeaveHashSet.Contains(id));

        return new WellnessMetricsDto
        {
            AvgOvertimeHoursPerMonth = Math.Round(avgOvertimePerMonth, 2),
            EmployeesWithExcessiveOvertime = employeesWithExcessiveOvertime,
            LeaveUtilizationPercent = Math.Round(leaveUtilizationPercent, 2),
            EmployeesWithNoLeaveTaken = employeesWithNoLeaveTaken,
            AbsenteeismRate = Math.Round(absenteeismRate, 2),
            EmployeesAtBurnoutRisk = employeesAtBurnoutRisk,
            TotalActiveEmployees = totalActive
        };
    }

    public async Task<EmployeeWellnessDto> GetEmployeeWellnessAsync(int employeeId)
    {
        var now = DateTime.UtcNow;
        var sixMonthsAgo = now.AddMonths(-6);
        var startOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endOfMonth = startOfMonth.AddMonths(1);
        var currentYear = now.Year;

        var overtimeThisMonth = await _db.OvertimeRequests
            .Where(o => o.EmployeeId == employeeId
                && o.Status == OvertimeRequestStatus.Approved
                && o.Date >= startOfMonth && o.Date < endOfMonth)
            .SumAsync(o => o.ActualHours ?? o.PlannedHours);

        var totalOvertimeLast6Months = await _db.OvertimeRequests
            .Where(o => o.EmployeeId == employeeId
                && o.Status == OvertimeRequestStatus.Approved
                && o.Date >= sixMonthsAgo)
            .SumAsync(o => o.ActualHours ?? o.PlannedHours);

        var leaveBalance = await _db.LeaveBalances
            .Where(l => l.EmployeeId == employeeId && l.Year == currentYear)
            .ToListAsync();

        var totalEntitled = leaveBalance.Sum(l => l.Entitled);
        var totalTaken = leaveBalance.Sum(l => l.Taken);
        var leaveUtilization = totalEntitled > 0 ? (double)totalTaken / (double)totalEntitled * 100.0 : 0;

        var leaveTakenLast6Months = await _db.LeaveRequests
            .Where(r => r.EmployeeId == employeeId
                && r.Status == LeaveRequestStatus.Approved
                && r.StartDate >= sixMonthsAgo)
            .SumAsync(r => r.NumberOfDays);

        var absencesThisMonth = await _db.AttendanceRecords
            .CountAsync(a => a.EmployeeId == employeeId
                && a.Date >= startOfMonth && a.Date < endOfMonth
                && a.AttendanceType == AttendanceType.Absent);

        var burnoutRisk = overtimeThisMonth > 15 && leaveTakenLast6Months == 0;

        return new EmployeeWellnessDto
        {
            EmployeeId = employeeId,
            OvertimeHoursThisMonth = overtimeThisMonth,
            TotalOvertimeLast6Months = totalOvertimeLast6Months,
            LeaveEntitledDays = totalEntitled,
            LeaveTakenDays = totalTaken,
            LeaveUtilizationPercent = Math.Round(leaveUtilization, 2),
            LeaveDaysTakenLast6Months = leaveTakenLast6Months,
            AbsencesThisMonth = absencesThisMonth,
            AtBurnoutRisk = burnoutRisk
        };
    }
}

public class WellnessMetricsDto
{
    public int TotalActiveEmployees { get; set; }
    public double AvgOvertimeHoursPerMonth { get; set; }
    public int EmployeesWithExcessiveOvertime { get; set; }
    public double LeaveUtilizationPercent { get; set; }
    public int EmployeesWithNoLeaveTaken { get; set; }
    public double AbsenteeismRate { get; set; }
    public int EmployeesAtBurnoutRisk { get; set; }
}

public class EmployeeWellnessDto
{
    public int EmployeeId { get; set; }
    public decimal OvertimeHoursThisMonth { get; set; }
    public decimal TotalOvertimeLast6Months { get; set; }
    public decimal LeaveEntitledDays { get; set; }
    public decimal LeaveTakenDays { get; set; }
    public double LeaveUtilizationPercent { get; set; }
    public decimal LeaveDaysTakenLast6Months { get; set; }
    public int AbsencesThisMonth { get; set; }
    public bool AtBurnoutRisk { get; set; }
}
