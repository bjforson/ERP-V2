using Microsoft.EntityFrameworkCore;

namespace NickFinance.Ledger;

/// <summary>
/// Create / open / close accounting periods. Period lifecycle is linear —
/// Open → SoftClosed → HardClosed — never backwards. Once hard-closed, the
/// only way to post to that period is an out-of-period adjustment event
/// against the next open period (not this service's concern yet).
/// </summary>
public interface IPeriodService
{
    Task<AccountingPeriod> CreateAsync(int year, byte month, long tenantId = 1, CancellationToken ct = default);
    Task<AccountingPeriod?> GetByDateAsync(DateOnly date, long tenantId = 1, CancellationToken ct = default);
    Task<AccountingPeriod> SoftCloseAsync(Guid periodId, Guid actorUserId, CancellationToken ct = default);
    Task<AccountingPeriod> HardCloseAsync(Guid periodId, Guid actorUserId, CancellationToken ct = default);
}

public class PeriodService : IPeriodService
{
    private readonly LedgerDbContext _db;
    private readonly TimeProvider _clock;

    public PeriodService(LedgerDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AccountingPeriod> CreateAsync(int year, byte month, long tenantId = 1, CancellationToken ct = default)
    {
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "Month must be 1..12.");

        var existing = await _db.Periods
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.FiscalYear == year && p.MonthNumber == month, ct);
        if (existing is not null) return existing;

        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var p = new AccountingPeriod
        {
            FiscalYear = year,
            MonthNumber = month,
            StartDate = start,
            EndDate = end,
            Status = PeriodStatus.Open,
            TenantId = tenantId
        };
        _db.Periods.Add(p);
        await _db.SaveChangesAsync(ct);
        return p;
    }

    public async Task<AccountingPeriod?> GetByDateAsync(DateOnly date, long tenantId = 1, CancellationToken ct = default)
        => await _db.Periods
            .FirstOrDefaultAsync(p => p.TenantId == tenantId
                                   && p.StartDate <= date
                                   && p.EndDate >= date, ct);

    public async Task<AccountingPeriod> SoftCloseAsync(Guid periodId, Guid actorUserId, CancellationToken ct = default)
    {
        var p = await _db.Periods.FindAsync(new object[] { periodId }, ct)
            ?? throw new LedgerException($"Period {periodId} not found.");
        if (p.Status == PeriodStatus.HardClosed)
            throw new LedgerException($"Period {periodId} is already HardClosed; can't go back to SoftClosed.");
        p.Status = PeriodStatus.SoftClosed;
        p.ClosedAt = _clock.GetUtcNow();
        p.ClosedByUserId = actorUserId;
        await _db.SaveChangesAsync(ct);
        return p;
    }

    public async Task<AccountingPeriod> HardCloseAsync(Guid periodId, Guid actorUserId, CancellationToken ct = default)
    {
        var p = await _db.Periods.FindAsync(new object[] { periodId }, ct)
            ?? throw new LedgerException($"Period {periodId} not found.");
        p.Status = PeriodStatus.HardClosed;
        p.ClosedAt ??= _clock.GetUtcNow();
        p.ClosedByUserId ??= actorUserId;
        await _db.SaveChangesAsync(ct);
        return p;
    }
}
