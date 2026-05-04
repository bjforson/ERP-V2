using Microsoft.EntityFrameworkCore;
using NickHR.Infrastructure.Data;
using System.Text;

namespace NickHR.Services.Payroll;

public class MobileMoneyPaymentService
{
    private readonly NickHRDbContext _db;

    public MobileMoneyPaymentService(NickHRDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Generates a Mobile Money payment CSV for a payroll run, filtered by network.
    /// Includes only employees who have a MobileMoneyNumber set.
    /// </summary>
    public async Task<byte[]> GenerateMoMoPaymentFileAsync(int payrollRunId, string network)
    {
        var run = await _db.PayrollRuns
            .Include(r => r.PayrollItems)
                .ThenInclude(i => i.Employee)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId)
            ?? throw new KeyNotFoundException($"Payroll run {payrollRunId} not found.");

        var sb = new StringBuilder();
        sb.AppendLine("PhoneNumber,Amount,Reference,FullName");

        foreach (var item in run.PayrollItems.OrderBy(i => i.Employee.EmployeeCode))
        {
            var emp = item.Employee;
            if (string.IsNullOrWhiteSpace(emp.MobileMoneyNumber)) continue;

            var phone = emp.MobileMoneyNumber.Trim();
            var amount = item.NetPay.ToString("F2");
            var reference = emp.EmployeeCode;
            var fullName = $"{emp.FirstName} {emp.LastName}".Replace(",", " ");

            sb.AppendLine($"{phone},{amount},{reference},{fullName}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Generates a bank transfer CSV for a payroll run.
    /// Includes all employees with bank account details.
    /// </summary>
    public async Task<byte[]> GenerateBankPaymentFileAsync(int payrollRunId)
    {
        var run = await _db.PayrollRuns
            .Include(r => r.PayrollItems)
                .ThenInclude(i => i.Employee)
            .FirstOrDefaultAsync(r => r.Id == payrollRunId)
            ?? throw new KeyNotFoundException($"Payroll run {payrollRunId} not found.");

        var sb = new StringBuilder();
        sb.AppendLine("BankName,BranchCode,AccountNumber,Amount,Reference,FullName");

        foreach (var item in run.PayrollItems.OrderBy(i => i.Employee.EmployeeCode))
        {
            var emp = item.Employee;
            if (string.IsNullOrWhiteSpace(emp.BankAccountNumber)) continue;

            var bankName = (emp.BankName ?? string.Empty).Replace(",", " ");
            var branchCode = (emp.BankBranch ?? string.Empty).Replace(",", " ");
            var accountNumber = emp.BankAccountNumber.Trim();
            var amount = item.NetPay.ToString("F2");
            var reference = emp.EmployeeCode;
            var fullName = $"{emp.FirstName} {emp.LastName}".Replace(",", " ");

            sb.AppendLine($"{bankName},{branchCode},{accountNumber},{amount},{reference},{fullName}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
