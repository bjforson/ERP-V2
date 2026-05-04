using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Leave;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.Attendance;

// ---------------------------------------------------------------------------
// DTOs (local to this service layer - kept simple and focused)
// ---------------------------------------------------------------------------

public class AttendanceRecordDto
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public DateTime? ClockIn { get; set; }
    public DateTime? ClockOut { get; set; }
    public AttendanceType AttendanceType { get; set; }
    public decimal? WorkHours { get; set; }
    public decimal? OvertimeHours { get; set; }
    public string? IPAddress { get; set; }
    public string? Notes { get; set; }
}

public class AttendanceReportEntryDto
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = string.Empty;
    public string? DepartmentName { get; set; }
    public int DaysPresent { get; set; }
    public int DaysAbsent { get; set; }
    public int LateCount { get; set; }
    public decimal TotalOvertimeHours { get; set; }
}

public class ClockInRequest
{
    public string? IpAddress { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

public interface IAttendanceService
{
    Task<AttendanceRecordDto> ClockInAsync(int employeeId, string? ipAddress, double? latitude, double? longitude);
    Task<AttendanceRecordDto> ClockOutAsync(int employeeId);
    Task<List<AttendanceRecordDto>> GetMyAttendanceAsync(int employeeId, int month, int year);
    Task<List<AttendanceReportEntryDto>> GetAttendanceReportAsync(int? departmentId, DateTime startDate, DateTime endDate);
    Task<AttendanceRecordDto?> GetTodayStatusAsync(int employeeId);
}

// ---------------------------------------------------------------------------
// Implementation
// ---------------------------------------------------------------------------

public class AttendanceService : IAttendanceService
{
    // Standard working hours per day; anything beyond this counts as overtime
    private const decimal StandardWorkHoursPerDay = 8m;

    private readonly NickHRDbContext _db;

    public AttendanceService(NickHRDbContext db)
    {
        _db = db;
    }

    public async Task<AttendanceRecordDto> ClockInAsync(
        int employeeId,
        string? ipAddress,
        double? latitude,
        double? longitude)
    {
        var today = DateTime.UtcNow.Date;
        var now = DateTime.UtcNow;

        // Validate employee exists
        var employee = await _db.Employees
            .Include(e => e.Location)
            .FirstOrDefaultAsync(e => e.Id == employeeId && !e.IsDeleted)
            ?? throw new KeyNotFoundException($"Employee with ID {employeeId} not found.");

        // Check not already clocked in today
        var existing = await _db.AttendanceRecords
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.Date == today && !a.IsDeleted);

        if (existing is not null && existing.ClockIn.HasValue)
            throw new InvalidOperationException("You have already clocked in today.");

        // Geo-fence validation when GPS coordinates are provided and the employee's
        // location has GPS coordinates configured
        if (latitude.HasValue && longitude.HasValue
            && employee.Location is not null
            && employee.Location.Latitude.HasValue
            && employee.Location.Longitude.HasValue)
        {
            var distanceMeters = HaversineDistanceMeters(
                latitude.Value, longitude.Value,
                employee.Location.Latitude.Value, employee.Location.Longitude.Value);

            if (distanceMeters > employee.Location.GeoFenceRadiusMeters)
                throw new InvalidOperationException(
                    $"Clock-in denied: you are {distanceMeters:F0} m from the office " +
                    $"(allowed radius: {employee.Location.GeoFenceRadiusMeters} m).");
        }

        // Determine attendance type (late if clocking in after 09:00 local-equivalent UTC)
        var attendanceType = now.TimeOfDay > new TimeSpan(9, 0, 0)
            ? AttendanceType.Late
            : AttendanceType.Present;

        AttendanceRecord record;
        if (existing is null)
        {
            record = new AttendanceRecord
            {
                EmployeeId = employeeId,
                Date = today,
                ClockIn = now,
                AttendanceType = attendanceType,
                IPAddress = ipAddress
            };
            _db.AttendanceRecords.Add(record);
        }
        else
        {
            // Record exists but ClockIn was not set yet
            existing.ClockIn = now;
            existing.AttendanceType = attendanceType;
            existing.IPAddress = ipAddress;
            record = existing;
        }

        await _db.SaveChangesAsync();

        return await MapToDtoAsync(record.Id);
    }

    public async Task<AttendanceRecordDto> ClockOutAsync(int employeeId)
    {
        var today = DateTime.UtcNow.Date;
        var now = DateTime.UtcNow;

        var record = await _db.AttendanceRecords
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.Date == today && !a.IsDeleted)
            ?? throw new InvalidOperationException("No clock-in record found for today. Please clock in first.");

        if (!record.ClockIn.HasValue)
            throw new InvalidOperationException("You have not clocked in today.");

        if (record.ClockOut.HasValue)
            throw new InvalidOperationException("You have already clocked out today.");

        record.ClockOut = now;

        // Calculate work hours
        var rawHours = (decimal)(now - record.ClockIn.Value).TotalHours;
        record.WorkHours = Math.Round(rawHours, 2);

        // Calculate overtime
        if (record.WorkHours > StandardWorkHoursPerDay)
            record.OvertimeHours = Math.Round(record.WorkHours.Value - StandardWorkHoursPerDay, 2);

        await _db.SaveChangesAsync();

        return await MapToDtoAsync(record.Id);
    }

    public async Task<List<AttendanceRecordDto>> GetMyAttendanceAsync(int employeeId, int month, int year)
    {
        var startDate = new DateTime(year, month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        var records = await _db.AttendanceRecords
            .Include(a => a.Employee)
            .Where(a => a.EmployeeId == employeeId
                        && !a.IsDeleted
                        && a.Date >= startDate
                        && a.Date <= endDate)
            .OrderBy(a => a.Date)
            .ToListAsync();

        return records.Select(MapToDto).ToList();
    }

    public async Task<List<AttendanceReportEntryDto>> GetAttendanceReportAsync(
        int? departmentId,
        DateTime startDate,
        DateTime endDate)
    {
        var employeeQuery = _db.Employees
            .Include(e => e.Department)
            .Where(e => !e.IsDeleted);

        if (departmentId.HasValue)
            employeeQuery = employeeQuery.Where(e => e.DepartmentId == departmentId.Value);

        var employees = await employeeQuery.ToListAsync();

        if (employees.Count == 0)
            return new List<AttendanceReportEntryDto>();

        var employeeIds = employees.Select(e => e.Id).ToList();

        var records = await _db.AttendanceRecords
            .Where(a => !a.IsDeleted
                        && employeeIds.Contains(a.EmployeeId)
                        && a.Date >= startDate.Date
                        && a.Date <= endDate.Date)
            .ToListAsync();

        // Calculate total working days in range (Mon-Fri only)
        var totalWorkingDays = CountWorkingDays(startDate.Date, endDate.Date);

        var result = new List<AttendanceReportEntryDto>();

        foreach (var emp in employees)
        {
            var empRecords = records.Where(r => r.EmployeeId == emp.Id).ToList();

            var daysPresent = empRecords.Count(r =>
                r.AttendanceType == AttendanceType.Present ||
                r.AttendanceType == AttendanceType.Late ||
                r.AttendanceType == AttendanceType.HalfDay);

            var lateCount = empRecords.Count(r => r.AttendanceType == AttendanceType.Late);

            var totalOvertime = empRecords
                .Where(r => r.OvertimeHours.HasValue)
                .Sum(r => r.OvertimeHours!.Value);

            result.Add(new AttendanceReportEntryDto
            {
                EmployeeId = emp.Id,
                EmployeeName = $"{emp.FirstName} {emp.LastName}".Trim(),
                DepartmentName = emp.Department?.Name,
                DaysPresent = daysPresent,
                DaysAbsent = Math.Max(0, totalWorkingDays - daysPresent),
                LateCount = lateCount,
                TotalOvertimeHours = Math.Round(totalOvertime, 2)
            });
        }

        return result.OrderBy(r => r.EmployeeName).ToList();
    }

    public async Task<AttendanceRecordDto?> GetTodayStatusAsync(int employeeId)
    {
        var today = DateTime.UtcNow.Date;

        var record = await _db.AttendanceRecords
            .Include(a => a.Employee)
            .FirstOrDefaultAsync(a => a.EmployeeId == employeeId && a.Date == today && !a.IsDeleted);

        return record is null ? null : MapToDto(record);
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Haversine formula: returns distance in metres between two GPS coordinates.
    /// </summary>
    private static double HaversineDistanceMeters(
        double lat1, double lon1,
        double lat2, double lon2)
    {
        const double EarthRadiusMeters = 6_371_000;

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static int CountWorkingDays(DateTime start, DateTime end)
    {
        var count = 0;
        var current = start;
        while (current <= end)
        {
            if (current.DayOfWeek != DayOfWeek.Saturday && current.DayOfWeek != DayOfWeek.Sunday)
                count++;
            current = current.AddDays(1);
        }
        return count;
    }

    private async Task<AttendanceRecordDto> MapToDtoAsync(int recordId)
    {
        var record = await _db.AttendanceRecords
            .Include(a => a.Employee)
            .FirstOrDefaultAsync(a => a.Id == recordId)
            ?? throw new InvalidOperationException($"Attendance record {recordId} could not be retrieved.");

        return MapToDto(record);
    }

    private static AttendanceRecordDto MapToDto(AttendanceRecord record) => new()
    {
        Id = record.Id,
        EmployeeId = record.EmployeeId,
        EmployeeName = $"{record.Employee.FirstName} {record.Employee.LastName}".Trim(),
        Date = record.Date,
        ClockIn = record.ClockIn,
        ClockOut = record.ClockOut,
        AttendanceType = record.AttendanceType,
        WorkHours = record.WorkHours,
        OvertimeHours = record.OvertimeHours,
        IPAddress = record.IPAddress,
        Notes = record.Notes
    };
}
