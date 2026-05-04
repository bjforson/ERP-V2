using Microsoft.EntityFrameworkCore;
using NickHR.Core.Enums;
using NickHR.Core.Interfaces;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Common;

public class AlertService : IAlertService
{
    private readonly NickHRDbContext _db;

    public AlertService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<List<UpcomingBirthdayDto>> GetUpcomingBirthdaysAsync(int days = 30)
    {
        var today = DateTime.Today;
        var activeStatuses = new[] { EmploymentStatus.Active, EmploymentStatus.Confirmed, EmploymentStatus.OnProbation };

        var employees = await _db.Employees
            .Where(e => !e.IsDeleted && activeStatuses.Contains(e.EmploymentStatus) && e.DateOfBirth.HasValue)
            .Select(e => new { e.Id, e.FirstName, e.LastName, e.DateOfBirth })
            .ToListAsync();

        var result = new List<UpcomingBirthdayDto>();
        foreach (var e in employees)
        {
            var dob = e.DateOfBirth!.Value;
            var thisYearBirthday = new DateTime(today.Year, dob.Month, dob.Day);
            if (thisYearBirthday < today)
                thisYearBirthday = thisYearBirthday.AddYears(1);

            var daysUntil = (thisYearBirthday - today).Days;
            if (daysUntil <= days)
            {
                var age = thisYearBirthday.Year - dob.Year;
                result.Add(new UpcomingBirthdayDto(e.Id, $"{e.FirstName} {e.LastName}", dob, daysUntil, age));
            }
        }

        return result.OrderBy(x => x.DaysUntil).ToList();
    }

    public async Task<List<UpcomingAnniversaryDto>> GetUpcomingAnniversariesAsync(int days = 30)
    {
        var today = DateTime.Today;
        var activeStatuses = new[] { EmploymentStatus.Active, EmploymentStatus.Confirmed, EmploymentStatus.OnProbation };

        var employees = await _db.Employees
            .Where(e => !e.IsDeleted && activeStatuses.Contains(e.EmploymentStatus) && e.HireDate.HasValue)
            .Select(e => new { e.Id, e.FirstName, e.LastName, e.HireDate })
            .ToListAsync();

        var result = new List<UpcomingAnniversaryDto>();
        foreach (var e in employees)
        {
            var hireDate = e.HireDate!.Value;
            if (hireDate.Year == today.Year && hireDate.Month == today.Month && hireDate.Day == today.Day) continue; // skip today if brand new

            var thisYearAnniversary = new DateTime(today.Year, hireDate.Month, hireDate.Day);
            if (thisYearAnniversary < today)
                thisYearAnniversary = thisYearAnniversary.AddYears(1);

            var daysUntil = (thisYearAnniversary - today).Days;
            if (daysUntil <= days)
            {
                var years = thisYearAnniversary.Year - hireDate.Year;
                result.Add(new UpcomingAnniversaryDto(e.Id, $"{e.FirstName} {e.LastName}", hireDate, daysUntil, years));
            }
        }

        return result.OrderBy(x => x.DaysUntil).ToList();
    }

    public async Task<List<ExpiringDocumentDto>> GetExpiringDocumentsAsync(int days = 60)
    {
        var today = DateTime.Today;
        var cutoff = today.AddDays(days);
        var activeStatuses = new[] { EmploymentStatus.Active, EmploymentStatus.Confirmed, EmploymentStatus.OnProbation };

        var employees = await _db.Employees
            .Where(e => !e.IsDeleted && activeStatuses.Contains(e.EmploymentStatus))
            .Select(e => new
            {
                e.Id,
                e.FirstName,
                e.LastName,
                e.PassportExpiry,
                e.DriversLicenseExpiry
            })
            .ToListAsync();

        var result = new List<ExpiringDocumentDto>();
        foreach (var e in employees)
        {
            if (e.PassportExpiry.HasValue && e.PassportExpiry.Value <= cutoff && e.PassportExpiry.Value >= today)
            {
                result.Add(new ExpiringDocumentDto(e.Id, $"{e.FirstName} {e.LastName}", "Passport", e.PassportExpiry.Value, (e.PassportExpiry.Value - today).Days));
            }
            if (e.DriversLicenseExpiry.HasValue && e.DriversLicenseExpiry.Value <= cutoff && e.DriversLicenseExpiry.Value >= today)
            {
                result.Add(new ExpiringDocumentDto(e.Id, $"{e.FirstName} {e.LastName}", "Driver's License", e.DriversLicenseExpiry.Value, (e.DriversLicenseExpiry.Value - today).Days));
            }
        }

        return result.OrderBy(x => x.DaysUntil).ToList();
    }

    public async Task<List<ExpiringContractDto>> GetExpiringContractsAsync(int days = 60)
    {
        // No ContractEndDate field on Employee; return empty list
        return await Task.FromResult(new List<ExpiringContractDto>());
    }
}
