using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;

namespace NickFinance.FixedAssets;

public interface IFixedAssetService
{
    Task<FixedAsset> RegisterAsync(RegisterAssetRequest req, CancellationToken ct = default);

    /// <summary>
    /// Compute + post the monthly depreciation journal for every asset
    /// active during <paramref name="month"/>. Idempotent: re-running for
    /// the same period returns 0 events posted.
    /// </summary>
    Task<int> PostMonthlyDepreciationAsync(int year, int month, Guid actorUserId, Guid periodId, long tenantId = 1, CancellationToken ct = default);

    Task<FixedAsset> DisposeAsync(Guid assetId, DateOnly disposedOn, long disposalProceedsMinor, Guid actorUserId, Guid periodId, CancellationToken ct = default);

    Task<IReadOnlyList<FixedAsset>> ListAsync(long tenantId = 1, AssetStatus? status = null, CancellationToken ct = default);
}

public sealed record RegisterAssetRequest(
    string AssetTag, string Name, AssetCategory Category,
    DateOnly AcquiredOn, long AcquisitionCostMinor, int UsefulLifeMonths,
    long SalvageValueMinor = 0, DepreciationMethod Method = DepreciationMethod.StraightLine,
    decimal DecliningBalanceRate = 0m,
    string CostAccount = "1500",
    string AccumulatedDepreciationAccount = "1510",
    string DepreciationExpenseAccount = "6700",
    Guid? SiteId = null, string? Notes = null,
    string CurrencyCode = "GHS", long TenantId = 1);

public sealed class FixedAssetException : Exception
{
    public FixedAssetException(string message) : base(message) { }
}

public sealed class FixedAssetService : IFixedAssetService
{
    private readonly FixedAssetsDbContext _db;
    private readonly ILedgerWriter _ledger;
    private readonly TimeProvider _clock;

    public FixedAssetService(FixedAssetsDbContext db, ILedgerWriter ledger, TimeProvider? clock = null)
    {
        _db = db;
        _ledger = ledger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<FixedAsset> RegisterAsync(RegisterAssetRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.AcquisitionCostMinor <= 0) throw new ArgumentException("Cost must be positive.", nameof(req));
        if (req.UsefulLifeMonths <= 0) throw new ArgumentException("UsefulLifeMonths must be positive.", nameof(req));
        if (req.SalvageValueMinor < 0 || req.SalvageValueMinor >= req.AcquisitionCostMinor)
        {
            throw new ArgumentException("SalvageValue must be in [0, cost).", nameof(req));
        }

        var existing = await _db.Assets.AnyAsync(a => a.TenantId == req.TenantId && a.AssetTag == req.AssetTag, ct);
        if (existing) throw new FixedAssetException($"Asset tag '{req.AssetTag}' already exists for tenant {req.TenantId}.");

        var now = _clock.GetUtcNow();
        var a = new FixedAsset
        {
            AssetTag = req.AssetTag.Trim(),
            Name = req.Name.Trim(),
            Category = req.Category,
            SiteId = req.SiteId,
            AcquiredOn = req.AcquiredOn,
            AcquisitionCostMinor = req.AcquisitionCostMinor,
            SalvageValueMinor = req.SalvageValueMinor,
            UsefulLifeMonths = req.UsefulLifeMonths,
            Method = req.Method,
            DecliningBalanceRate = req.DecliningBalanceRate,
            CostAccount = req.CostAccount,
            AccumulatedDepreciationAccount = req.AccumulatedDepreciationAccount,
            DepreciationExpenseAccount = req.DepreciationExpenseAccount,
            CurrencyCode = req.CurrencyCode,
            Status = AssetStatus.Active,
            Notes = req.Notes,
            CreatedAt = now,
            UpdatedAt = now,
            TenantId = req.TenantId
        };
        _db.Assets.Add(a);
        await _db.SaveChangesAsync(ct);
        return a;
    }

    public async Task<int> PostMonthlyDepreciationAsync(int year, int month, Guid actorUserId, Guid periodId, long tenantId = 1, CancellationToken ct = default)
    {
        var monthEnd = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var assets = await _db.Assets
            .Where(a => a.TenantId == tenantId && a.Status == AssetStatus.Active && a.AcquiredOn <= monthEnd)
            .ToListAsync(ct);

        var posted = 0;
        foreach (var a in assets)
        {
            // Don't depreciate before acquisition month or after end of life or after a previous post for this month.
            var startMonth = new DateOnly(a.AcquiredOn.Year, a.AcquiredOn.Month, 1);
            if (monthEnd < startMonth) continue;
            if (a.LastDepreciatedThrough is { } last && last >= monthEnd) continue;
            if (a.AccumulatedDepreciationMinor + 1 >= a.AcquisitionCostMinor - a.SalvageValueMinor) continue;

            var monthly = ComputeMonthlyDepreciation(a);
            var remaining = a.AcquisitionCostMinor - a.SalvageValueMinor - a.AccumulatedDepreciationMinor;
            if (monthly > remaining) monthly = remaining;
            if (monthly <= 0) continue;

            var ev = new LedgerEvent
            {
                TenantId = tenantId,
                EffectiveDate = monthEnd,
                PeriodId = periodId,
                SourceModule = "fixed_assets",
                SourceEntityType = "FixedAsset",
                SourceEntityId = a.FixedAssetId.ToString("N"),
                IdempotencyKey = $"fa:{a.FixedAssetId:N}:dep:{year:D4}{month:D2}",
                Narration = $"Depreciation {a.AssetTag} {year}-{month:D2}",
                ActorUserId = actorUserId,
                Lines =
                {
                    new LedgerEventLine { LineNo = 1, AccountCode = a.DepreciationExpenseAccount,
                        DebitMinor = monthly, CurrencyCode = a.CurrencyCode,
                        Description = $"Depreciation {a.AssetTag} {year}-{month:D2}" },
                    new LedgerEventLine { LineNo = 2, AccountCode = a.AccumulatedDepreciationAccount,
                        CreditMinor = monthly, CurrencyCode = a.CurrencyCode,
                        Description = $"Accumulated depreciation — {a.AssetTag}" }
                }
            };
            await _ledger.PostAsync(ev, ct);

            a.AccumulatedDepreciationMinor += monthly;
            a.LastDepreciatedThrough = monthEnd;
            a.UpdatedAt = _clock.GetUtcNow();
            posted++;
        }
        if (posted > 0) await _db.SaveChangesAsync(ct);
        return posted;
    }

    public async Task<FixedAsset> DisposeAsync(Guid assetId, DateOnly disposedOn, long disposalProceedsMinor, Guid actorUserId, Guid periodId, CancellationToken ct = default)
    {
        var a = await _db.Assets.FirstOrDefaultAsync(x => x.FixedAssetId == assetId, ct)
            ?? throw new FixedAssetException($"Asset {assetId} not found.");
        if (a.Status != AssetStatus.Active) throw new FixedAssetException($"Asset already {a.Status}.");
        if (disposalProceedsMinor < 0) throw new ArgumentException("Proceeds cannot be negative.", nameof(disposalProceedsMinor));

        // Disposal journal:
        //   DR  cash (1010)                       proceeds
        //   DR  acc dep (1510)                    accumulated
        //   CR  asset cost (1500)                 cost
        //   DR/CR gain or loss on disposal (4900 / 6900)
        var nbv = a.AcquisitionCostMinor - a.AccumulatedDepreciationMinor;
        var gainLoss = disposalProceedsMinor - nbv;     // positive = gain, negative = loss

        var ev = new LedgerEvent
        {
            TenantId = a.TenantId,
            EffectiveDate = disposedOn,
            PeriodId = periodId,
            SourceModule = "fixed_assets",
            SourceEntityType = "FixedAssetDisposal",
            SourceEntityId = a.FixedAssetId.ToString("N"),
            IdempotencyKey = $"fa:{a.FixedAssetId:N}:dispose",
            Narration = $"Disposal of {a.AssetTag}",
            ActorUserId = actorUserId
        };
        short ln = 1;
        if (disposalProceedsMinor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = "1010",
                DebitMinor = disposalProceedsMinor, CurrencyCode = a.CurrencyCode,
                Description = $"Proceeds from disposal of {a.AssetTag}"
            });
        }
        if (a.AccumulatedDepreciationMinor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = a.AccumulatedDepreciationAccount,
                DebitMinor = a.AccumulatedDepreciationMinor, CurrencyCode = a.CurrencyCode,
                Description = $"Reverse accumulated depreciation — {a.AssetTag}"
            });
        }
        ev.Lines.Add(new LedgerEventLine
        {
            LineNo = ln++, AccountCode = a.CostAccount,
            CreditMinor = a.AcquisitionCostMinor, CurrencyCode = a.CurrencyCode,
            Description = $"Reverse cost — {a.AssetTag}"
        });
        if (gainLoss > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln, AccountCode = "4900",
                CreditMinor = gainLoss, CurrencyCode = a.CurrencyCode,
                Description = $"Gain on disposal of {a.AssetTag}"
            });
        }
        else if (gainLoss < 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln, AccountCode = "6900",
                DebitMinor = -gainLoss, CurrencyCode = a.CurrencyCode,
                Description = $"Loss on disposal of {a.AssetTag}"
            });
        }
        await _ledger.PostAsync(ev, ct);

        a.Status = AssetStatus.Disposed;
        a.DisposedOn = disposedOn;
        a.DisposalProceedsMinor = disposalProceedsMinor;
        a.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);
        return a;
    }

    public async Task<IReadOnlyList<FixedAsset>> ListAsync(long tenantId = 1, AssetStatus? status = null, CancellationToken ct = default)
    {
        var q = _db.Assets.Where(a => a.TenantId == tenantId);
        if (status is not null) q = q.Where(a => a.Status == status);
        return await q.OrderBy(a => a.AssetTag).ToListAsync(ct);
    }

    private static long ComputeMonthlyDepreciation(FixedAsset a)
    {
        var depBase = a.AcquisitionCostMinor - a.SalvageValueMinor;
        return a.Method switch
        {
            DepreciationMethod.StraightLine => depBase / Math.Max(1, a.UsefulLifeMonths),
            DepreciationMethod.DecliningBalance =>
                (long)Math.Round(((decimal)(a.AcquisitionCostMinor - a.AccumulatedDepreciationMinor) * a.DecliningBalanceRate) / 12m,
                    0, MidpointRounding.ToEven),
            _ => 0L
        };
    }
}
