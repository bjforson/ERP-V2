using Microsoft.EntityFrameworkCore;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Analytics;

public class TurnoverAnalyticsService
{
    private readonly NickHRDbContext _db;

    public TurnoverAnalyticsService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<List<TurnoverRiskResult>> GetTurnoverRiskAsync()
    {
        var employees = await _db.Employees
            .Where(e => e.EmploymentStatus == EmploymentStatus.Active)
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .ToListAsync();

        var results = new List<TurnoverRiskResult>();

        foreach (var emp in employees)
        {
            var riskScore = 0.0;
            var factors = new List<string>();

            // Tenure factor: employees with 1-2 years are higher risk
            if (emp.HireDate.HasValue)
            {
                var tenure = (DateTime.UtcNow - emp.HireDate.Value).TotalDays / 365.25;
                if (tenure >= 1 && tenure <= 2)
                {
                    riskScore += 20;
                    factors.Add("Early career stage (1-2 years)");
                }
                else if (tenure > 5)
                {
                    riskScore -= 10;
                }
            }

            // Check overtime: high overtime = burnout risk
            var recentOvertime = await _db.OvertimeRequests
                .Where(o => o.EmployeeId == emp.Id && o.CreatedAt >= DateTime.UtcNow.AddMonths(-3))
                .SumAsync(o => (decimal?)o.ActualHours ?? 0);

            if (recentOvertime > 40)
            {
                riskScore += 25;
                factors.Add("High overtime (>40hrs in 3 months)");
            }

            // Check leave patterns: lots of sick leave
            var recentLeave = await _db.LeaveRequests
                .Where(l => l.EmployeeId == emp.Id
                    && l.Status == LeaveRequestStatus.Approved
                    && l.CreatedAt >= DateTime.UtcNow.AddMonths(-6))
                .CountAsync();

            if (recentLeave > 5)
            {
                riskScore += 15;
                factors.Add("Frequent leave requests");
            }

            // Performance: check latest appraisal
            var latestAppraisal = await _db.AppraisalForms
                .Where(a => a.EmployeeId == emp.Id)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync();

            if (latestAppraisal != null && latestAppraisal.FinalRating < 2.5m)
            {
                riskScore += 20;
                factors.Add("Low performance rating");
            }

            // No recent salary change (stagnation)
            var recentTransfer = await _db.TransferPromotions
                .Where(t => t.EmployeeId == emp.Id && t.CreatedAt >= DateTime.UtcNow.AddYears(-1))
                .AnyAsync();

            if (!recentTransfer && emp.HireDate.HasValue && (DateTime.UtcNow - emp.HireDate.Value).TotalDays > 730)
            {
                riskScore += 15;
                factors.Add("No promotion/transfer in 2+ years");
            }

            var level = riskScore >= 50 ? RiskLevel.High
                      : riskScore >= 25 ? RiskLevel.Medium
                                        : RiskLevel.Low;

            results.Add(new TurnoverRiskResult
            {
                EmployeeId = emp.Id,
                EmployeeName = $"{emp.FirstName} {emp.LastName}",
                EmployeeCode = emp.EmployeeCode,
                Department = emp.Department?.Name ?? "N/A",
                Designation = emp.Designation?.Title ?? "N/A",
                RiskScore = Math.Max(0, Math.Min(100, riskScore)),
                RiskLevel = level.ToString(),
                Factors = factors
            });
        }

        return results.OrderByDescending(r => r.RiskScore).ToList();
    }

    public async Task<List<DepartmentRiskSummary>> GetDepartmentHeatmapAsync()
    {
        var risks = await GetTurnoverRiskAsync();

        return risks
            .GroupBy(r => r.Department)
            .Select(g => new DepartmentRiskSummary
            {
                Department = g.Key,
                TotalEmployees = g.Count(),
                HighRisk = g.Count(r => r.RiskLevel == nameof(RiskLevel.High)),
                MediumRisk = g.Count(r => r.RiskLevel == nameof(RiskLevel.Medium)),
                LowRisk = g.Count(r => r.RiskLevel == nameof(RiskLevel.Low)),
                AverageScore = g.Average(r => r.RiskScore)
            })
            .OrderByDescending(d => d.AverageScore)
            .ToList();
    }
}

public class TurnoverRiskResult
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string EmployeeCode { get; set; } = "";
    public string Department { get; set; } = "";
    public string Designation { get; set; } = "";
    public double RiskScore { get; set; }
    public string RiskLevel { get; set; } = nameof(NickHR.Core.Enums.RiskLevel.Low);
    public List<string> Factors { get; set; } = new();
}

public class DepartmentRiskSummary
{
    public string Department { get; set; } = "";
    public int TotalEmployees { get; set; }
    public int HighRisk { get; set; }
    public int MediumRisk { get; set; }
    public int LowRisk { get; set; }
    public double AverageScore { get; set; }
}
