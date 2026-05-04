using Microsoft.EntityFrameworkCore;
using NickHR.Core.Constants;
using NickHR.Core.Entities.Core;
using NickHR.Core.Entities.Payroll;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;
using NickHR.Services.Payroll.Calculators;
using NickHR.Services.Payroll.Models;

namespace NickHR.Services.Payroll;

public interface IPayrollProcessingService
{
    /// <summary>Run payroll for all active employees for a given month/year.</summary>
    Task<PayrollRun> RunPayrollAsync(int month, int year, string processedBy);

    /// <summary>Preview payroll calculation for a single employee (no DB save).</summary>
    Task<PayrollCalculationResult> PreviewEmployeePayrollAsync(int employeeId, int month, int year);

    /// <summary>Get a completed payroll run with all items.</summary>
    Task<PayrollRun?> GetPayrollRunAsync(int payrollRunId);

    /// <summary>Get all payroll runs.</summary>
    Task<List<PayrollRun>> GetPayrollHistoryAsync();

    /// <summary>Lock a payroll run to prevent further changes.</summary>
    Task LockPayrollAsync(int payrollRunId, string approvedBy);

    /// <summary>Reverse a payroll run.</summary>
    Task ReversePayrollAsync(int payrollRunId, string reversedBy);

    /// <summary>Get payslip data for an employee in a specific payroll run.</summary>
    Task<PayrollCalculationResult?> GetPayslipAsync(int payrollRunId, int employeeId);

    /// <summary>Generate SSNIT monthly report data.</summary>
    Task<SSNITMonthlyReport> GenerateSSNITReportAsync(int month, int year);

    /// <summary>Generate GRA PAYE monthly report data.</summary>
    Task<PAYEMonthlyReport> GeneratePAYEReportAsync(int month, int year);

    /// <summary>Generate Tier 2 pension report for a month/year.</summary>
    Task<Tier2Report> GenerateTier2ReportAsync(int month, int year);

    /// <summary>Generate Tier 3 pension report for a month/year.</summary>
    Task<Tier3Report> GenerateTier3ReportAsync(int month, int year);

    /// <summary>Generate GRA annual return for a full year.</summary>
    Task<AnnualReturnReport> GenerateAnnualReturnAsync(int year);
}

public class PayrollProcessingService : IPayrollProcessingService
{
    private readonly NickHRDbContext _context;

    public PayrollProcessingService(NickHRDbContext context)
    {
        _context = context;
    }

    public async Task<PayrollRun> RunPayrollAsync(int month, int year, string processedBy)
    {
        // Check if payroll already exists for this period
        var existing = await _context.PayrollRuns
            .FirstOrDefaultAsync(p => p.PayPeriodMonth == month && p.PayPeriodYear == year && !p.IsDeleted);

        if (existing != null && existing.Status == PayrollStatus.Locked)
            throw new InvalidOperationException($"Payroll for {month}/{year} is already locked.");

        if (existing != null && existing.Status != PayrollStatus.Reversed)
        {
            // Wave 2I: wrap the three-step delete in a transaction so a crash
            // mid-cleanup can't leave orphaned PayrollItem rows pointing at a
            // PayrollRun that's been removed (or vice versa).
            await using var tx = await _context.Database.BeginTransactionAsync();
            try
            {
                _context.PayrollItemDetails.RemoveRange(
                    _context.PayrollItemDetails.Where(d => d.PayrollItem.PayrollRunId == existing.Id));
                _context.PayrollItems.RemoveRange(
                    _context.PayrollItems.Where(i => i.PayrollRunId == existing.Id));
                _context.PayrollRuns.Remove(existing);
                await _context.SaveChangesAsync();

                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }

        // Get all active employees
        var employees = await _context.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .Where(e => !e.IsDeleted &&
                        (e.EmploymentStatus == EmploymentStatus.Active ||
                         e.EmploymentStatus == EmploymentStatus.Confirmed ||
                         e.EmploymentStatus == EmploymentStatus.OnProbation))
            .ToListAsync();

        // Create payroll run
        var payrollRun = new PayrollRun
        {
            PayPeriodMonth = month,
            PayPeriodYear = year,
            RunDate = DateTime.UtcNow,
            Status = PayrollStatus.Processing,
            ProcessedBy = processedBy
        };
        _context.PayrollRuns.Add(payrollRun);
        await _context.SaveChangesAsync();

        decimal totalGross = 0, totalNet = 0, totalSSNITEe = 0, totalSSNITEr = 0, totalPAYE = 0, totalDeductions = 0;

        foreach (var employee in employees)
        {
            var result = await CalculateEmployeePayroll(employee, month, year);

            var payrollItem = new PayrollItem
            {
                PayrollRunId = payrollRun.Id,
                EmployeeId = employee.Id,
                BasicSalary = result.BasicSalary,
                TotalAllowances = result.TotalAllowances,
                GrossPay = result.GrossPay,
                SSNITEmployee = result.SSNITEmployee,
                SSNITEmployer = result.SSNITEmployer,
                TaxableIncome = result.TaxableIncome,
                PAYE = result.PAYE,
                TotalDeductions = result.TotalDeductions,
                NetPay = result.NetPay,
                OvertimeHours = result.OvertimeHours,
                OvertimePay = result.OvertimePay
            };
            _context.PayrollItems.Add(payrollItem);
            await _context.SaveChangesAsync();

            // Save line item details
            foreach (var allowance in result.Allowances)
            {
                _context.PayrollItemDetails.Add(new PayrollItemDetail
                {
                    PayrollItemId = payrollItem.Id,
                    ComponentName = allowance.Name,
                    ComponentType = nameof(SalaryComponentType.Earning),
                    Amount = allowance.Amount
                });
            }

            // SSNIT as detail
            _context.PayrollItemDetails.Add(new PayrollItemDetail
            {
                PayrollItemId = payrollItem.Id,
                ComponentName = PayrollComponents.SsnitEmployeeName,
                ComponentType = nameof(SalaryComponentType.Deduction),
                Amount = result.SSNITEmployee
            });

            _context.PayrollItemDetails.Add(new PayrollItemDetail
            {
                PayrollItemId = payrollItem.Id,
                ComponentName = PayrollComponents.PayeTaxName,
                ComponentType = nameof(SalaryComponentType.Deduction),
                Amount = result.PAYE
            });

            foreach (var deduction in result.Deductions)
            {
                _context.PayrollItemDetails.Add(new PayrollItemDetail
                {
                    PayrollItemId = payrollItem.Id,
                    ComponentName = deduction.Name,
                    ComponentType = nameof(SalaryComponentType.Deduction),
                    Amount = deduction.Amount
                });
            }

            totalGross += result.GrossPay;
            totalNet += result.NetPay;
            totalSSNITEe += result.SSNITEmployee;
            totalSSNITEr += result.SSNITEmployer;
            totalPAYE += result.PAYE;
            totalDeductions += result.TotalDeductions;
        }

        await _context.SaveChangesAsync();

        // Update payroll run totals
        payrollRun.Status = PayrollStatus.Completed;
        payrollRun.TotalGrossPay = totalGross;
        payrollRun.TotalNetPay = totalNet;
        payrollRun.TotalSSNITEmployee = totalSSNITEe;
        payrollRun.TotalSSNITEmployer = totalSSNITEr;
        payrollRun.TotalPAYE = totalPAYE;
        payrollRun.TotalDeductions = totalDeductions;
        await _context.SaveChangesAsync();

        return payrollRun;
    }

    public async Task<PayrollCalculationResult> PreviewEmployeePayrollAsync(int employeeId, int month, int year)
    {
        var employee = await _context.Employees
            .Include(e => e.Department)
            .Include(e => e.Designation)
            .FirstOrDefaultAsync(e => e.Id == employeeId && !e.IsDeleted)
            ?? throw new InvalidOperationException("Employee not found.");

        return await CalculateEmployeePayroll(employee, month, year);
    }

    private async Task<PayrollCalculationResult> CalculateEmployeePayroll(Employee employee, int month, int year)
    {
        var result = new PayrollCalculationResult
        {
            EmployeeId = employee.Id,
            EmployeeCode = employee.EmployeeCode,
            EmployeeName = $"{employee.FirstName} {employee.LastName}",
            Department = employee.Department?.Name ?? "",
            Designation = employee.Designation?.Title ?? "",
            BasicSalary = employee.BasicSalary,
            BankName = employee.BankName,
            BankAccountNumber = employee.BankAccountNumber,
            MobileMoneyNumber = employee.MobileMoneyNumber
        };

        // 1. Get salary structure (allowances)
        var salaryStructure = await _context.EmployeeSalaryStructures
            .Include(s => s.SalaryComponent)
            .Where(s => s.EmployeeId == employee.Id && s.IsActive &&
                        s.EffectiveFrom <= new DateTime(year, month, 1) &&
                        (s.EffectiveTo == null || s.EffectiveTo >= new DateTime(year, month, 1)))
            .ToListAsync();

        foreach (var item in salaryStructure)
        {
            if (item.SalaryComponent.ComponentType == SalaryComponentType.Earning)
            {
                result.Allowances.Add(new PayrollLineItem
                {
                    Code = item.SalaryComponent.Code,
                    Name = item.SalaryComponent.Name,
                    Amount = item.Amount,
                    IsTaxable = item.SalaryComponent.IsTaxable,
                    IsStatutory = item.SalaryComponent.IsStatutory
                });
            }
        }

        // 2. Calculate overtime from attendance records
        var overtimeRecords = await _context.AttendanceRecords
            .Where(a => a.EmployeeId == employee.Id &&
                        a.Date.Month == month && a.Date.Year == year &&
                        a.OvertimeHours > 0)
            .ToListAsync();

        result.OvertimeHours = overtimeRecords.Sum(o => o.OvertimeHours ?? 0);
        if (result.OvertimeHours > 0)
        {
            // Overtime rate: 1.5x hourly rate (basic / 22 days / 8 hours)
            decimal hourlyRate = employee.BasicSalary / 22m / 8m;
            result.OvertimePay = Math.Round(result.OvertimeHours * hourlyRate * 1.5m, 2);
        }

        // 3. Calculate SSNIT contributions
        var (ssnitEe, ssnitEr, _) = SSNITCalculator.Calculate(employee.BasicSalary);
        result.SSNITEmployee = ssnitEe;
        result.SSNITEmployer = ssnitEr;

        // 4. Calculate taxable income
        decimal nonTaxableAllowances = result.Allowances
            .Where(a => !a.IsTaxable)
            .Sum(a => a.Amount);
        result.TaxableIncome = SSNITCalculator.CalculateTaxableIncome(
            result.GrossPay, result.SSNITEmployee, nonTaxableAllowances);

        // 5. Calculate PAYE
        var (paye, taxBreakdown) = GhanaPAYECalculator.Calculate(result.TaxableIncome);
        result.PAYE = paye;
        result.TaxBreakdown = taxBreakdown;

        // 6. Calculate overtime tax for junior staff
        if (result.OvertimePay > 0)
        {
            decimal overtimeTax = GhanaPAYECalculator.CalculateOvertimeTax(
                result.OvertimePay, employee.BasicSalary, employee.BasicSalary * 12);
            if (overtimeTax > 0)
            {
                result.PAYE += overtimeTax;
                result.TaxBreakdown.Add(new TaxBracketBreakdown
                {
                    Bracket = "Overtime Tax (Junior Staff)",
                    TaxableAmount = result.OvertimePay,
                    Rate = 5, // Simplified display
                    Tax = overtimeTax
                });
            }
        }

        // 7. Get active loan deductions
        var activeLoans = await _context.Loans
            .Where(l => l.EmployeeId == employee.Id &&
                        l.LoanStatus == LoanStatus.Active &&
                        l.BalanceRemaining > 0)
            .ToListAsync();

        foreach (var loan in activeLoans)
        {
            decimal deduction = Math.Min(loan.MonthlyInstallment, loan.BalanceRemaining);
            result.Deductions.Add(new PayrollLineItem
            {
                Code = "LOAN",
                Name = $"Loan: {loan.LoanType}",
                Amount = deduction,
                IsTaxable = false,
                IsStatutory = false
            });
        }

        // 8. Get other salary structure deductions (union dues, welfare, provident fund, etc.)
        foreach (var item in salaryStructure)
        {
            if (item.SalaryComponent.ComponentType == SalaryComponentType.Deduction &&
                !item.SalaryComponent.IsStatutory) // Statutory (SSNIT) already calculated
            {
                result.Deductions.Add(new PayrollLineItem
                {
                    Code = item.SalaryComponent.Code,
                    Name = item.SalaryComponent.Name,
                    Amount = item.Amount,
                    IsTaxable = false,
                    IsStatutory = false
                });
            }
        }

        // Warnings
        if (result.NetPay < 0)
            result.Warnings.Add("Net pay is negative - deductions exceed earnings.");
        if (employee.BankAccountNumber == null && employee.MobileMoneyNumber == null)
            result.Warnings.Add("No bank account or mobile money number configured.");

        return result;
    }

    public async Task<PayrollRun?> GetPayrollRunAsync(int payrollRunId)
    {
        return await _context.PayrollRuns
            .Include(p => p.PayrollItems)
                .ThenInclude(i => i.Employee)
            .Include(p => p.PayrollItems)
                .ThenInclude(i => i.PayrollItemDetails)
            .FirstOrDefaultAsync(p => p.Id == payrollRunId && !p.IsDeleted);
    }

    public async Task<List<PayrollRun>> GetPayrollHistoryAsync()
    {
        return await _context.PayrollRuns
            .Where(p => !p.IsDeleted)
            .OrderByDescending(p => p.PayPeriodYear)
            .ThenByDescending(p => p.PayPeriodMonth)
            .ToListAsync();
    }

    public async Task LockPayrollAsync(int payrollRunId, string approvedBy)
    {
        var run = await _context.PayrollRuns.FindAsync(payrollRunId)
            ?? throw new InvalidOperationException("Payroll run not found.");

        if (run.Status != PayrollStatus.Completed)
            throw new InvalidOperationException("Only completed payroll runs can be locked.");

        run.Status = PayrollStatus.Locked;
        run.ApprovedBy = approvedBy;
        run.ApprovedAt = DateTime.UtcNow;

        // Update loan balances
        var payrollItems = await _context.PayrollItems
            .Include(i => i.PayrollItemDetails)
            .Where(i => i.PayrollRunId == payrollRunId)
            .ToListAsync();

        foreach (var item in payrollItems)
        {
            var loanDeductions = item.PayrollItemDetails
                .Where(d => d.ComponentName.StartsWith("Loan:"));

            foreach (var loanDeduction in loanDeductions)
            {
                var loan = await _context.Loans
                    .FirstOrDefaultAsync(l => l.EmployeeId == item.EmployeeId &&
                                              l.LoanStatus == LoanStatus.Active);
                if (loan != null)
                {
                    loan.BalanceRemaining -= loanDeduction.Amount;
                    if (loan.BalanceRemaining <= 0)
                    {
                        loan.BalanceRemaining = 0;
                        loan.LoanStatus = LoanStatus.FullyPaid;
                    }

                    _context.LoanRepayments.Add(new LoanRepayment
                    {
                        LoanId = loan.Id,
                        PayrollRunId = payrollRunId,
                        Amount = loanDeduction.Amount,
                        RepaymentDate = DateTime.UtcNow,
                        BalanceAfter = loan.BalanceRemaining
                    });
                }
            }
        }

        await _context.SaveChangesAsync();
    }

    public async Task ReversePayrollAsync(int payrollRunId, string reversedBy)
    {
        var run = await _context.PayrollRuns.FindAsync(payrollRunId)
            ?? throw new InvalidOperationException("Payroll run not found.");

        if (run.Status == PayrollStatus.Reversed)
            throw new InvalidOperationException("Payroll is already reversed.");

        run.Status = PayrollStatus.Reversed;
        run.Notes = $"Reversed by {reversedBy} on {DateTime.UtcNow:yyyy-MM-dd HH:mm}";
        await _context.SaveChangesAsync();
    }

    public async Task<PayrollCalculationResult?> GetPayslipAsync(int payrollRunId, int employeeId)
    {
        var item = await _context.PayrollItems
            .Include(i => i.Employee).ThenInclude(e => e.Department)
            .Include(i => i.Employee).ThenInclude(e => e.Designation)
            .Include(i => i.PayrollItemDetails)
            .FirstOrDefaultAsync(i => i.PayrollRunId == payrollRunId && i.EmployeeId == employeeId);

        if (item == null) return null;

        var result = new PayrollCalculationResult
        {
            EmployeeId = item.EmployeeId,
            EmployeeCode = item.Employee.EmployeeCode,
            EmployeeName = $"{item.Employee.FirstName} {item.Employee.LastName}",
            Department = item.Employee.Department?.Name ?? "",
            Designation = item.Employee.Designation?.Title ?? "",
            BasicSalary = item.BasicSalary,
            SSNITEmployee = item.SSNITEmployee,
            SSNITEmployer = item.SSNITEmployer,
            TaxableIncome = item.TaxableIncome,
            PAYE = item.PAYE,
            OvertimeHours = item.OvertimeHours,
            OvertimePay = item.OvertimePay,
            BankName = item.Employee.BankName,
            BankAccountNumber = item.Employee.BankAccountNumber,
            MobileMoneyNumber = item.Employee.MobileMoneyNumber
        };

        foreach (var detail in item.PayrollItemDetails)
        {
            if (detail.ComponentType == nameof(SalaryComponentType.Earning))
            {
                result.Allowances.Add(new PayrollLineItem
                {
                    Name = detail.ComponentName,
                    Amount = detail.Amount
                });
            }
            else if (detail.ComponentType == nameof(SalaryComponentType.Deduction) &&
                     detail.ComponentName != PayrollComponents.SsnitEmployeeName &&
                     detail.ComponentName != PayrollComponents.PayeTaxName)
            {
                result.Deductions.Add(new PayrollLineItem
                {
                    Name = detail.ComponentName,
                    Amount = detail.Amount
                });
            }
        }

        return result;
    }

    public async Task<SSNITMonthlyReport> GenerateSSNITReportAsync(int month, int year)
    {
        var payrollRun = await _context.PayrollRuns
            .Include(p => p.PayrollItems).ThenInclude(i => i.Employee)
            .FirstOrDefaultAsync(p => p.PayPeriodMonth == month && p.PayPeriodYear == year &&
                                      !p.IsDeleted && p.Status != PayrollStatus.Reversed);

        if (payrollRun == null)
            throw new InvalidOperationException($"No payroll found for {month}/{year}.");

        var report = new SSNITMonthlyReport
        {
            Month = month,
            Year = year,
            GeneratedAt = DateTime.UtcNow
        };

        foreach (var item in payrollRun.PayrollItems)
        {
            report.Entries.Add(new SSNITReportEntry
            {
                SSNITNumber = item.Employee.SSNITNumber ?? "N/A",
                EmployeeName = $"{item.Employee.FirstName} {item.Employee.LastName}",
                BasicSalary = item.BasicSalary,
                EmployeeContribution = item.SSNITEmployee,
                EmployerContribution = item.SSNITEmployer,
                TotalContribution = item.SSNITEmployee + item.SSNITEmployer
            });
        }

        report.TotalEmployeeContribution = report.Entries.Sum(e => e.EmployeeContribution);
        report.TotalEmployerContribution = report.Entries.Sum(e => e.EmployerContribution);
        report.TotalContribution = report.TotalEmployeeContribution + report.TotalEmployerContribution;

        return report;
    }

    public async Task<PAYEMonthlyReport> GeneratePAYEReportAsync(int month, int year)
    {
        var payrollRun = await _context.PayrollRuns
            .Include(p => p.PayrollItems).ThenInclude(i => i.Employee)
            .FirstOrDefaultAsync(p => p.PayPeriodMonth == month && p.PayPeriodYear == year &&
                                      !p.IsDeleted && p.Status != PayrollStatus.Reversed);

        if (payrollRun == null)
            throw new InvalidOperationException($"No payroll found for {month}/{year}.");

        var report = new PAYEMonthlyReport
        {
            Month = month,
            Year = year,
            GeneratedAt = DateTime.UtcNow
        };

        foreach (var item in payrollRun.PayrollItems)
        {
            report.Entries.Add(new PAYEReportEntry
            {
                TIN = item.Employee.TIN ?? "N/A",
                EmployeeName = $"{item.Employee.FirstName} {item.Employee.LastName}",
                GrossPay = item.GrossPay,
                TaxableIncome = item.TaxableIncome,
                PAYEDeducted = item.PAYE
            });
        }

        report.TotalGrossPay = report.Entries.Sum(e => e.GrossPay);
        report.TotalTaxableIncome = report.Entries.Sum(e => e.TaxableIncome);
        report.TotalPAYE = report.Entries.Sum(e => e.PAYEDeducted);

        return report;
    }

    public async Task<Tier2Report> GenerateTier2ReportAsync(int month, int year)
    {
        var payrollRun = await _context.PayrollRuns
            .Include(p => p.PayrollItems).ThenInclude(i => i.Employee)
            .FirstOrDefaultAsync(p => p.PayPeriodMonth == month && p.PayPeriodYear == year &&
                                      !p.IsDeleted && p.Status != PayrollStatus.Reversed);

        if (payrollRun == null)
            throw new InvalidOperationException($"No payroll found for {month}/{year}.");

        var report = new Tier2Report { Month = month, Year = year, GeneratedAt = DateTime.UtcNow };

        foreach (var item in payrollRun.PayrollItems.Where(i => i.Employee.Tier2PensionNumber != null))
        {
            var contribution = Math.Round(item.BasicSalary * 0.05m, 2);
            report.Entries.Add(new Tier2Entry
            {
                Tier2PensionNumber = item.Employee.Tier2PensionNumber!,
                Provider = item.Employee.Tier2Provider ?? "N/A",
                EmployeeName = $"{item.Employee.FirstName} {item.Employee.LastName}",
                BasicSalary = item.BasicSalary,
                Contribution = contribution
            });
        }

        report.TotalContribution = report.Entries.Sum(e => e.Contribution);
        return report;
    }

    public async Task<Tier3Report> GenerateTier3ReportAsync(int month, int year)
    {
        var payrollRun = await _context.PayrollRuns
            .Include(p => p.PayrollItems).ThenInclude(i => i.Employee)
            .Include(p => p.PayrollItems).ThenInclude(i => i.PayrollItemDetails)
            .FirstOrDefaultAsync(p => p.PayPeriodMonth == month && p.PayPeriodYear == year &&
                                      !p.IsDeleted && p.Status != PayrollStatus.Reversed);

        if (payrollRun == null)
            throw new InvalidOperationException($"No payroll found for {month}/{year}.");

        var report = new Tier3Report { Month = month, Year = year, GeneratedAt = DateTime.UtcNow };

        foreach (var item in payrollRun.PayrollItems.Where(i => i.Employee.Tier3PensionNumber != null))
        {
            // Tier 3: voluntary contributions — sourced from salary deduction details if available, otherwise 0
            var voluntaryContribution = item.PayrollItemDetails
                .Where(d => d.ComponentName.Contains("Tier 3", StringComparison.OrdinalIgnoreCase)
                         || d.ComponentName.Contains("Tier3", StringComparison.OrdinalIgnoreCase))
                .Sum(d => d.Amount);

            report.Entries.Add(new Tier3Entry
            {
                Tier3PensionNumber = item.Employee.Tier3PensionNumber!,
                Provider = item.Employee.Tier3Provider ?? "N/A",
                EmployeeName = $"{item.Employee.FirstName} {item.Employee.LastName}",
                VoluntaryContribution = voluntaryContribution
            });
        }

        report.TotalContribution = report.Entries.Sum(e => e.VoluntaryContribution);
        return report;
    }

    public async Task<AnnualReturnReport> GenerateAnnualReturnAsync(int year)
    {
        var payrollRuns = await _context.PayrollRuns
            .Include(p => p.PayrollItems).ThenInclude(i => i.Employee)
            .Where(p => p.PayPeriodYear == year && !p.IsDeleted && p.Status != PayrollStatus.Reversed)
            .ToListAsync();

        var report = new AnnualReturnReport { Year = year, GeneratedAt = DateTime.UtcNow };

        // Aggregate per employee
        var byEmployee = payrollRuns
            .SelectMany(r => r.PayrollItems)
            .GroupBy(i => i.EmployeeId);

        foreach (var group in byEmployee)
        {
            var first = group.First();
            report.Entries.Add(new AnnualReturnEntry
            {
                EmployeeId = group.Key,
                EmployeeName = $"{first.Employee.FirstName} {first.Employee.LastName}",
                TIN = first.Employee.TIN ?? "N/A",
                TotalGrossPay = group.Sum(i => i.GrossPay),
                TotalTaxPaid = group.Sum(i => i.PAYE),
                TotalSSNIT = group.Sum(i => i.SSNITEmployee + i.SSNITEmployer)
            });
        }

        report.Entries = report.Entries.OrderBy(e => e.EmployeeName).ToList();
        report.GrandTotalGross = report.Entries.Sum(e => e.TotalGrossPay);
        report.GrandTotalTax = report.Entries.Sum(e => e.TotalTaxPaid);
        report.GrandTotalSSNIT = report.Entries.Sum(e => e.TotalSSNIT);
        return report;
    }
}

// Report models
public class SSNITMonthlyReport
{
    public int Month { get; set; }
    public int Year { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<SSNITReportEntry> Entries { get; set; } = new();
    public decimal TotalEmployeeContribution { get; set; }
    public decimal TotalEmployerContribution { get; set; }
    public decimal TotalContribution { get; set; }
}

public class SSNITReportEntry
{
    public string SSNITNumber { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }
    public decimal EmployeeContribution { get; set; }
    public decimal EmployerContribution { get; set; }
    public decimal TotalContribution { get; set; }
}

public class PAYEMonthlyReport
{
    public int Month { get; set; }
    public int Year { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<PAYEReportEntry> Entries { get; set; } = new();
    public decimal TotalGrossPay { get; set; }
    public decimal TotalTaxableIncome { get; set; }
    public decimal TotalPAYE { get; set; }
}

public class PAYEReportEntry
{
    public string TIN { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public decimal GrossPay { get; set; }
    public decimal TaxableIncome { get; set; }
    public decimal PAYEDeducted { get; set; }
}

// Tier 2 Pension
public class Tier2Report
{
    public int Month { get; set; }
    public int Year { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<Tier2Entry> Entries { get; set; } = new();
    public decimal TotalContribution { get; set; }
}

public class Tier2Entry
{
    public string Tier2PensionNumber { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public decimal BasicSalary { get; set; }
    public decimal Contribution { get; set; }
}

// Tier 3 Pension
public class Tier3Report
{
    public int Month { get; set; }
    public int Year { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<Tier3Entry> Entries { get; set; } = new();
    public decimal TotalContribution { get; set; }
}

public class Tier3Entry
{
    public string Tier3PensionNumber { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string EmployeeName { get; set; } = string.Empty;
    public decimal VoluntaryContribution { get; set; }
}

// GRA Annual Return
public class AnnualReturnReport
{
    public int Year { get; set; }
    public DateTime GeneratedAt { get; set; }
    public List<AnnualReturnEntry> Entries { get; set; } = new();
    public decimal GrandTotalGross { get; set; }
    public decimal GrandTotalTax { get; set; }
    public decimal GrandTotalSSNIT { get; set; }
}

public class AnnualReturnEntry
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string TIN { get; set; } = string.Empty;
    public decimal TotalGrossPay { get; set; }
    public decimal TotalTaxPaid { get; set; }
    public decimal TotalSSNIT { get; set; }
}
