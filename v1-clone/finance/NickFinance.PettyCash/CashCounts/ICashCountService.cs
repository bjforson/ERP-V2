using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;

namespace NickFinance.PettyCash.CashCounts;

/// <summary>
/// Records and reads custodian cash counts. The system-of-record balance
/// comes from the Ledger reader — this service never trusts a caller-supplied
/// "expected" amount, it always computes from journals.
/// </summary>
public interface ICashCountService
{
    /// <summary>
    /// Snapshot the float's current Ledger balance, persist a <see cref="CashCount"/>
    /// row with the physical figure provided by the custodian, and return the
    /// variance. Throws if there's a non-zero variance and no
    /// <paramref name="varianceReason"/> was supplied.
    /// </summary>
    Task<CashCount> RecordAsync(
        Guid floatId,
        long physicalAmountMinor,
        string currencyCode,
        Guid countedByUserId,
        Guid? witnessUserId,
        string? varianceReason,
        DateOnly asOfDate,
        long tenantId = 1,
        CancellationToken ct = default);

    /// <summary>List counts for a float, most recent first.</summary>
    Task<IReadOnlyList<CashCount>> ListAsync(Guid floatId, int take = 30, CancellationToken ct = default);
}

public sealed class CashCountService : ICashCountService
{
    private readonly PettyCashDbContext _db;
    private readonly ILedgerReader _ledger;
    private readonly TimeProvider _clock;

    public CashCountService(PettyCashDbContext db, ILedgerReader ledger, TimeProvider? clock = null)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<CashCount> RecordAsync(
        Guid floatId,
        long physicalAmountMinor,
        string currencyCode,
        Guid countedByUserId,
        Guid? witnessUserId,
        string? varianceReason,
        DateOnly asOfDate,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        if (physicalAmountMinor < 0)
        {
            throw new ArgumentException("Physical amount cannot be negative.", nameof(physicalAmountMinor));
        }
        if (witnessUserId == countedByUserId)
        {
            throw new SeparationOfDutiesException("The witness must differ from the counter.");
        }

        var fl = await _db.Floats.FirstOrDefaultAsync(f => f.FloatId == floatId && f.TenantId == tenantId, ct)
            ?? throw new PettyCashException($"Float {floatId} not found for tenant {tenantId}.");
        if (!fl.IsActive)
        {
            throw new PettyCashException("Cannot count a closed float.");
        }
        if (!string.Equals(fl.CurrencyCode, currencyCode, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Currency mismatch: float is {fl.CurrencyCode}, count was in {currencyCode}.",
                nameof(currencyCode));
        }

        // 1060 Petty cash float — the kernel reader gives us the running balance
        // for the account up to the count date. Negative balance == cash drawn
        // out without offsetting top-ups, so the system-amount is -balance.
        // Convention: ledger sign is DR positive, CR negative on a normal account.
        // 1060 is an asset normal-debit; sign matches "current cash on hand" directly.
        var rawBalance = await _ledger.GetAccountBalanceAsync("1060", currencyCode, asOfDate, tenantId, ct);
        var systemAmountMinor = rawBalance.Minor;

        var variance = physicalAmountMinor - systemAmountMinor;
        if (variance != 0 && string.IsNullOrWhiteSpace(varianceReason))
        {
            throw new PettyCashException(
                $"Cash count variance is {variance} minor units; a justification is required.");
        }

        var entity = new CashCount
        {
            FloatId = floatId,
            CountedByUserId = countedByUserId,
            WitnessUserId = witnessUserId,
            CountedAt = _clock.GetUtcNow(),
            PhysicalAmountMinor = physicalAmountMinor,
            SystemAmountMinor = systemAmountMinor,
            CurrencyCode = currencyCode,
            VarianceReason = varianceReason,
            TenantId = tenantId
        };
        _db.CashCounts.Add(entity);
        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<IReadOnlyList<CashCount>> ListAsync(Guid floatId, int take = 30, CancellationToken ct = default)
    {
        if (take < 1) take = 1;
        if (take > 200) take = 200;
        return await _db.CashCounts
            .Where(c => c.FloatId == floatId)
            .OrderByDescending(c => c.CountedAt)
            .Take(take)
            .ToListAsync(ct);
    }
}
