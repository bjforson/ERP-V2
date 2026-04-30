using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;

namespace NickFinance.PettyCash.Recurring;

/// <summary>
/// Walks active <see cref="RecurringVoucherTemplate"/> rows and submits
/// the next voucher for any template whose next-fire date has passed.
/// Idempotent: each template tracks <see cref="RecurringVoucherTemplate.LastFiredOn"/>
/// so a second run on the same day is a no-op.
/// </summary>
public interface IRecurringVoucherRunner
{
    /// <summary>
    /// Fire all due templates with effective date <paramref name="today"/>.
    /// Returns the number of vouchers submitted.
    /// </summary>
    Task<int> RunAsync(DateOnly today, long tenantId = 1, CancellationToken ct = default);
}

public sealed class RecurringVoucherRunner : IRecurringVoucherRunner
{
    private readonly PettyCashDbContext _db;
    private readonly IPettyCashService _pc;

    public RecurringVoucherRunner(PettyCashDbContext db, IPettyCashService pc)
    {
        _db = db;
        _pc = pc;
    }

    public async Task<int> RunAsync(DateOnly today, long tenantId = 1, CancellationToken ct = default)
    {
        var templates = await _db.Set<RecurringVoucherTemplate>()
            .Where(t => t.TenantId == tenantId && t.IsActive)
            .ToListAsync(ct);
        var fired = 0;
        foreach (var t in templates)
        {
            if (t.EndDate is { } end && end < today) continue;
            if (today < t.StartDate) continue;

            var dueOn = NextDue(t);
            if (dueOn is null || dueOn > today) continue;

            await _pc.SubmitVoucherAsync(new SubmitVoucherRequest(
                FloatId: t.FloatId,
                RequesterUserId: t.RequesterUserId,
                Category: t.Category,
                Purpose: t.Purpose,
                Amount: new Money(t.AmountMinor, t.CurrencyCode),
                Lines: new[] { new VoucherLineInput(t.Purpose, new Money(t.AmountMinor, t.CurrencyCode)) },
                PayeeName: t.PayeeName,
                ProjectCode: t.ProjectCode,
                TenantId: t.TenantId), ct);
            t.LastFiredOn = dueOn;
            fired++;
        }
        if (fired > 0) await _db.SaveChangesAsync(ct);
        return fired;
    }

    private static DateOnly? NextDue(RecurringVoucherTemplate t)
    {
        var anchor = t.LastFiredOn ?? t.StartDate.AddDays(-1);
        return t.Frequency switch
        {
            RecurrenceFrequency.Daily    => anchor.AddDays(1),
            RecurrenceFrequency.Weekly   => anchor.AddDays(7),
            RecurrenceFrequency.BiWeekly => anchor.AddDays(14),
            RecurrenceFrequency.Monthly  => anchor.AddMonths(1),
            _ => null
        };
    }
}
