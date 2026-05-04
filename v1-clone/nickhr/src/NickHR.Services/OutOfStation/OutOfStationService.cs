using Microsoft.EntityFrameworkCore;
using NickHR.Core.Entities.Payroll;
using NickHR.Core.Enums;
using NickHR.Infrastructure.Data;

namespace NickHR.Services.OutOfStation;

public class OutOfStationAllowanceBreakdown
{
    public int NumberOfNights { get; set; }
    public int NumberOfDays { get; set; }
    public decimal AccommodationRate { get; set; }
    public decimal FeedingRate { get; set; }
    public decimal TransportRate { get; set; }
    public decimal MiscellaneousRate { get; set; }
    public decimal AccommodationTotal { get; set; }
    public decimal FeedingTotal { get; set; }
    public decimal TransportTotal { get; set; }
    public decimal MiscellaneousTotal { get; set; }
    public decimal TotalAllowance { get; set; }
    public string GradeName { get; set; } = string.Empty;
}

public class OutOfStationService
{
    private readonly NickHRDbContext _db;

    public OutOfStationService(NickHRDbContext db)
    {
        _db = db;
    }

    // ─── Rate Management ────────────────────────────────────────────────────

    public async Task<List<OutOfStationRate>> GetRatesAsync()
    {
        return await _db.OutOfStationRates
            .Include(r => r.Grade)
            .Where(r => !r.IsDeleted)
            .OrderBy(r => r.Grade.Level)
            .ThenBy(r => r.DestinationType)
            .ToListAsync();
    }

    public async Task<OutOfStationRate?> GetRateForGradeAsync(int gradeId, OutOfStationDestType destType)
    {
        return await _db.OutOfStationRates
            .Include(r => r.Grade)
            .Where(r => r.GradeId == gradeId
                     && r.DestinationType == destType
                     && r.IsActive
                     && !r.IsDeleted
                     && (r.EffectiveTo == null || r.EffectiveTo >= DateTime.UtcNow))
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync();
    }

    public async Task<OutOfStationRate> CreateOrUpdateRateAsync(
        int gradeId, OutOfStationDestType destType,
        decimal accommodation, decimal feeding,
        decimal roadTransport, decimal airTransport, decimal misc)
    {
        var existing = await _db.OutOfStationRates
            .FirstOrDefaultAsync(r => r.GradeId == gradeId
                                   && r.DestinationType == destType
                                   && r.IsActive
                                   && !r.IsDeleted);

        if (existing != null)
        {
            existing.AccommodationRate = accommodation;
            existing.FeedingRate = feeding;
            existing.TransportRoadRate = roadTransport;
            existing.TransportAirRate = airTransport;
            existing.MiscellaneousRate = misc;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new OutOfStationRate
            {
                GradeId = gradeId,
                DestinationType = destType,
                AccommodationRate = accommodation,
                FeedingRate = feeding,
                TransportRoadRate = roadTransport,
                TransportAirRate = airTransport,
                MiscellaneousRate = misc,
                EffectiveFrom = DateTime.UtcNow,
                IsActive = true
            };
            _db.OutOfStationRates.Add(existing);
        }

        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task SeedDefaultRatesAsync()
    {
        var anyExist = await _db.OutOfStationRates.AnyAsync(r => !r.IsDeleted);
        if (anyExist) return;

        var grades = await _db.Grades
            .Where(g => g.IsActive && !g.IsDeleted)
            .OrderBy(g => g.Level)
            .ToListAsync();

        // Default domestic rates per grade level (GHS)
        // Grade level 1 = senior, higher number = junior - adjust as needed
        var defaultRates = new[]
        {
            new { Accommodation = 350m, Feeding = 120m, Road = 80m, Air = 800m, Misc = 50m },  // Grade 1
            new { Accommodation = 300m, Feeding = 100m, Road = 70m, Air = 700m, Misc = 40m },  // Grade 2
            new { Accommodation = 250m, Feeding = 90m,  Road = 60m, Air = 600m, Misc = 35m },  // Grade 3
            new { Accommodation = 220m, Feeding = 80m,  Road = 55m, Air = 550m, Misc = 30m },  // Grade 4
            new { Accommodation = 200m, Feeding = 70m,  Road = 50m, Air = 500m, Misc = 25m },  // Grade 5
            new { Accommodation = 180m, Feeding = 60m,  Road = 45m, Air = 450m, Misc = 20m },  // Grade 6
            new { Accommodation = 160m, Feeding = 55m,  Road = 40m, Air = 400m, Misc = 18m },  // Grade 7
            new { Accommodation = 140m, Feeding = 50m,  Road = 35m, Air = 350m, Misc = 15m },  // Grade 8
        };

        var now = DateTime.UtcNow;

        for (int i = 0; i < grades.Count; i++)
        {
            var r = i < defaultRates.Length ? defaultRates[i] : defaultRates[defaultRates.Length - 1];
            var grade = grades[i];

            // Domestic
            _db.OutOfStationRates.Add(new OutOfStationRate
            {
                GradeId = grade.Id,
                DestinationType = OutOfStationDestType.Domestic,
                AccommodationRate = r.Accommodation,
                FeedingRate = r.Feeding,
                TransportRoadRate = r.Road,
                TransportAirRate = r.Air,
                MiscellaneousRate = r.Misc,
                EffectiveFrom = now,
                IsActive = true
            });

            // International (2.5x domestic)
            _db.OutOfStationRates.Add(new OutOfStationRate
            {
                GradeId = grade.Id,
                DestinationType = OutOfStationDestType.International,
                AccommodationRate = r.Accommodation * 2.5m,
                FeedingRate = r.Feeding * 2.5m,
                TransportRoadRate = r.Road * 2.5m,
                TransportAirRate = r.Air * 2.5m,
                MiscellaneousRate = r.Misc * 2.5m,
                EffectiveFrom = now,
                IsActive = true
            });
        }

        await _db.SaveChangesAsync();
    }

    // ─── Request Management ─────────────────────────────────────────────────

    public async Task<OutOfStationAllowanceBreakdown> CalculateAllowanceAsync(
        int employeeId,
        OutOfStationDestType destType,
        DateTime departureDate,
        DateTime returnDate,
        TransportMode transportMode)
    {
        var employee = await _db.Employees
            .Include(e => e.Grade)
            .FirstOrDefaultAsync(e => e.Id == employeeId)
            ?? throw new KeyNotFoundException("Employee not found.");

        if (employee.GradeId == null)
            throw new InvalidOperationException("Employee has no grade assigned.");

        var rate = await GetRateForGradeAsync(employee.GradeId.Value, destType)
            ?? throw new InvalidOperationException($"No active rate found for grade '{employee.Grade?.Name}' and destination type '{destType}'.");

        int nights = (int)(returnDate.Date - departureDate.Date).TotalDays;
        if (nights < 0) nights = 0;
        int days = nights + 1;

        var accommodation = rate.AccommodationRate * nights;
        var feeding = rate.FeedingRate * days;
        var transport = (transportMode == TransportMode.Air ? rate.TransportAirRate : rate.TransportRoadRate) * 2;
        var misc = rate.MiscellaneousRate * days;
        var total = accommodation + feeding + transport + misc;

        return new OutOfStationAllowanceBreakdown
        {
            NumberOfNights = nights,
            NumberOfDays = days,
            AccommodationRate = rate.AccommodationRate,
            FeedingRate = rate.FeedingRate,
            TransportRate = transportMode == TransportMode.Air ? rate.TransportAirRate : rate.TransportRoadRate,
            MiscellaneousRate = rate.MiscellaneousRate,
            AccommodationTotal = accommodation,
            FeedingTotal = feeding,
            TransportTotal = transport,
            MiscellaneousTotal = misc,
            TotalAllowance = total,
            GradeName = employee.Grade?.Name ?? string.Empty
        };
    }

    public async Task<OutOfStationRequest> SubmitRequestAsync(
        int employeeId,
        string destination,
        OutOfStationDestType destType,
        string purpose,
        DateTime departureDate,
        DateTime returnDate,
        TransportMode transportMode)
    {
        var breakdown = await CalculateAllowanceAsync(employeeId, destType, departureDate, returnDate, transportMode);

        var request = new OutOfStationRequest
        {
            EmployeeId = employeeId,
            Destination = destination,
            DestinationType = destType,
            Purpose = purpose,
            DepartureDate = departureDate,
            ReturnDate = returnDate,
            NumberOfDays = breakdown.NumberOfDays,
            NumberOfNights = breakdown.NumberOfNights,
            TransportMode = transportMode,
            AccommodationTotal = breakdown.AccommodationTotal,
            FeedingTotal = breakdown.FeedingTotal,
            TransportTotal = breakdown.TransportTotal,
            MiscellaneousTotal = breakdown.MiscellaneousTotal,
            TotalAllowance = breakdown.TotalAllowance,
            Status = OutOfStationStatus.Pending
        };

        _db.OutOfStationRequests.Add(request);
        await _db.SaveChangesAsync();
        return request;
    }

    public async Task<List<OutOfStationRequest>> GetMyRequestsAsync(int employeeId)
    {
        return await _db.OutOfStationRequests
            .Include(r => r.ApprovedBy)
            .Where(r => r.EmployeeId == employeeId && !r.IsDeleted)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<OutOfStationRequest>> GetPendingApprovalsAsync()
    {
        return await _db.OutOfStationRequests
            .Include(r => r.Employee)
            .ThenInclude(e => e.Grade)
            .Where(r => r.Status == OutOfStationStatus.Pending && !r.IsDeleted)
            .OrderBy(r => r.DepartureDate)
            .ToListAsync();
    }

    public async Task<List<OutOfStationRequest>> GetAllRequestsAsync()
    {
        return await _db.OutOfStationRequests
            .Include(r => r.Employee)
            .Include(r => r.ApprovedBy)
            .Where(r => !r.IsDeleted)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();
    }

    public async Task<OutOfStationRequest?> GetByIdAsync(int id)
    {
        return await _db.OutOfStationRequests
            .Include(r => r.Employee)
            .ThenInclude(e => e.Grade)
            .Include(r => r.ApprovedBy)
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted);
    }

    public async Task ApproveAsync(int id, int approverId, decimal? advanceAmount = null)
    {
        var request = await _db.OutOfStationRequests.FindAsync(id)
            ?? throw new KeyNotFoundException("Request not found.");

        request.Status = OutOfStationStatus.Approved;
        request.ApprovedById = approverId;
        request.ApprovedAt = DateTime.UtcNow;
        request.AdvancePaid = advanceAmount ?? request.TotalAllowance;
        request.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task RejectAsync(int id, int approverId, string reason)
    {
        var request = await _db.OutOfStationRequests.FindAsync(id)
            ?? throw new KeyNotFoundException("Request not found.");

        request.Status = OutOfStationStatus.Rejected;
        request.ApprovedById = approverId;
        request.ApprovedAt = DateTime.UtcNow;
        request.RejectionReason = reason;
        request.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task CompleteAsync(int id)
    {
        var request = await _db.OutOfStationRequests.FindAsync(id)
            ?? throw new KeyNotFoundException("Request not found.");

        request.Status = OutOfStationStatus.Completed;
        request.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task SettleAsync(int id, decimal actualExpenses, string? receiptPaths = null)
    {
        var request = await _db.OutOfStationRequests.FindAsync(id)
            ?? throw new KeyNotFoundException("Request not found.");

        request.ActualExpenses = actualExpenses;
        // Positive = reimbursement to employee, Negative = refund to company
        request.SettlementAmount = actualExpenses - request.AdvancePaid;
        request.SettledAt = DateTime.UtcNow;
        request.Status = OutOfStationStatus.Settled;
        request.UpdatedAt = DateTime.UtcNow;
        if (receiptPaths != null)
            request.ReceiptPaths = receiptPaths;

        await _db.SaveChangesAsync();
    }
}
