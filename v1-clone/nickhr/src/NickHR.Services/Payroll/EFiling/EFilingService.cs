using System.Text;
using Microsoft.EntityFrameworkCore;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Payroll.EFiling;

public class EFilingService
{
    private readonly NickHRDbContext _db;

    public EFilingService(NickHRDbContext db)
    {
        _db = db;
    }

    /// <summary>Generate SSNIT e-filing CSV for a given month/year.</summary>
    public async Task<byte[]> GenerateSSNITEFileAsync(int month, int year)
    {
        // Wave 2L: AsNoTracking — read-only export, skip the change-tracker.
        var run = await _db.PayrollRuns
            .AsNoTracking()
            .Include(r => r.PayrollItems).ThenInclude(i => i.Employee)
            .FirstOrDefaultAsync(r => r.PayPeriodMonth == month && r.PayPeriodYear == year)
            ?? throw new InvalidOperationException($"No payroll run found for {month}/{year}.");

        var sb = new StringBuilder();
        sb.AppendLine("EmployerSSNITNumber,EmployeeName,SSNITNumber,BasicSalary,EmployeeContrib,EmployerContrib");

        // Get employer SSNIT number from company settings
        var employerSsnit = await _db.CompanySettings
            .AsNoTracking()
            .Where(c => c.Key == "EmployerSSNITNumber")
            .Select(c => c.Value)
            .FirstOrDefaultAsync() ?? "EMPLOYER_SSNIT";

        foreach (var item in run.PayrollItems.OrderBy(i => i.Employee.LastName))
        {
            var emp = item.Employee;
            var name = $"{emp.LastName} {emp.FirstName}";
            sb.AppendLine($"{employerSsnit},{name},{emp.SSNITNumber ?? ""},{item.BasicSalary:F2},{item.SSNITEmployee:F2},{item.SSNITEmployer:F2}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Generate GRA PAYE monthly e-filing CSV.</summary>
    public async Task<byte[]> GenerateGRAEFileAsync(int month, int year)
    {
        var run = await _db.PayrollRuns
            .AsNoTracking()
            .Include(r => r.PayrollItems).ThenInclude(i => i.Employee)
            .FirstOrDefaultAsync(r => r.PayPeriodMonth == month && r.PayPeriodYear == year)
            ?? throw new InvalidOperationException($"No payroll run found for {month}/{year}.");

        var sb = new StringBuilder();
        sb.AppendLine("TIN,EmployeeName,GrossIncome,TaxableIncome,TAXDeducted");

        foreach (var item in run.PayrollItems.OrderBy(i => i.Employee.LastName))
        {
            var emp = item.Employee;
            var name = $"{emp.LastName} {emp.FirstName}";
            sb.AppendLine($"{emp.TIN ?? ""},{name},{item.GrossPay:F2},{item.TaxableIncome:F2},{item.PAYE:F2}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Generate GRA annual return CSV for a full year.</summary>
    public async Task<byte[]> GenerateGRAAnnualEFileAsync(int year)
    {
        // Wave 2L performance: pre-aggregate at the SQL layer using GroupBy +
        // SUM rather than pulling every PayrollItem into memory and looping.
        // Joins PayrollItems → Employee in a single query; Postgres returns one
        // row per employee with the totals already computed.
        //
        // Note: we still need the Year filter joined through PayrollRun, hence
        // the join on PayrollRunId.

        var anyRun = await _db.PayrollRuns
            .AsNoTracking()
            .AnyAsync(r => r.PayPeriodYear == year);
        if (!anyRun)
            throw new InvalidOperationException($"No payroll runs found for year {year}.");

        var aggregated = await (
            from item in _db.PayrollItems.AsNoTracking()
            join run in _db.PayrollRuns.AsNoTracking() on item.PayrollRunId equals run.Id
            join emp in _db.Employees.AsNoTracking() on item.EmployeeId equals emp.Id
            where run.PayPeriodYear == year
            group new { item, emp } by new { emp.Id, emp.TIN, emp.FirstName, emp.LastName } into g
            select new
            {
                g.Key.TIN,
                Name = g.Key.LastName + " " + g.Key.FirstName,
                TotalGross = g.Sum(x => x.item.GrossPay),
                TotalTaxable = g.Sum(x => x.item.TaxableIncome),
                TotalTax = g.Sum(x => x.item.PAYE)
            }
        ).OrderBy(x => x.Name).ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("TIN,EmployeeName,AnnualGrossIncome,AnnualTaxableIncome,AnnualTAXDeducted");

        foreach (var v in aggregated)
        {
            sb.AppendLine($"{v.TIN ?? ""},{v.Name},{v.TotalGross:F2},{v.TotalTaxable:F2},{v.TotalTax:F2}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
