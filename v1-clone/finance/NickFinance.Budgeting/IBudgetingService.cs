using Microsoft.EntityFrameworkCore;
using NickFinance.AP;
using NickFinance.AR;
using NickFinance.Ledger;

namespace NickFinance.Budgeting;

public interface IBudgetingService
{
    Task<AnnualBudget> CreateAsync(CreateBudgetRequest req, CancellationToken ct = default);
    Task<AnnualBudget> UpsertLinesAsync(Guid budgetId, IReadOnlyList<UpsertBudgetLine> lines, CancellationToken ct = default);
    Task<AnnualBudget> ApproveAsync(Guid budgetId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>
    /// Variance per (account, month) for a budget — budget minus actuals
    /// from the live ledger.
    /// </summary>
    Task<IReadOnlyList<BudgetVarianceRow>> VarianceAsync(Guid budgetId, CancellationToken ct = default);

    /// <summary>
    /// 13-week rolling cash forecast — receipts come from issued AR
    /// invoices' due dates, payments from approved AP bills' due dates.
    /// </summary>
    Task<IReadOnlyList<CashForecastRow>> CashForecast13WeeksAsync(DateOnly from, long tenantId = 1, CancellationToken ct = default);
}

public sealed record CreateBudgetRequest(
    int FiscalYear, string Name, Guid CreatedByUserId,
    string? DepartmentCode = null, string CurrencyCode = "GHS", long TenantId = 1);

public sealed record UpsertBudgetLine(string AccountCode, byte MonthNumber, long AmountMinor);

public sealed class BudgetingException : Exception
{
    public BudgetingException(string message) : base(message) { }
}

public sealed class BudgetingService : IBudgetingService
{
    private readonly BudgetingDbContext _db;
    private readonly LedgerDbContext _ledger;
    private readonly ArDbContext? _ar;
    private readonly ApDbContext? _ap;
    private readonly TimeProvider _clock;

    public BudgetingService(BudgetingDbContext db, LedgerDbContext ledger, ArDbContext? ar = null, ApDbContext? ap = null, TimeProvider? clock = null)
    {
        _db = db; _ledger = ledger; _ar = ar; _ap = ap;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AnnualBudget> CreateAsync(CreateBudgetRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Name required.", nameof(req));
        var existing = await _db.Budgets.AnyAsync(b =>
            b.TenantId == req.TenantId && b.FiscalYear == req.FiscalYear && b.DepartmentCode == req.DepartmentCode, ct);
        if (existing) throw new BudgetingException($"Budget for {req.FiscalYear}/{req.DepartmentCode ?? "(entity)"} already exists.");
        var b = new AnnualBudget
        {
            FiscalYear = req.FiscalYear, Name = req.Name.Trim(),
            DepartmentCode = req.DepartmentCode, CurrencyCode = req.CurrencyCode,
            Status = BudgetStatus.Draft,
            CreatedAt = _clock.GetUtcNow(), CreatedByUserId = req.CreatedByUserId,
            TenantId = req.TenantId
        };
        _db.Budgets.Add(b);
        await _db.SaveChangesAsync(ct);
        return b;
    }

    public async Task<AnnualBudget> UpsertLinesAsync(Guid budgetId, IReadOnlyList<UpsertBudgetLine> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var b = await _db.Budgets.Include(x => x.Lines).FirstOrDefaultAsync(x => x.AnnualBudgetId == budgetId, ct)
            ?? throw new BudgetingException($"Budget {budgetId} not found.");
        if (b.Status == BudgetStatus.Closed) throw new BudgetingException("Closed budget; cannot edit lines.");
        foreach (var l in lines)
        {
            if (l.MonthNumber is < 1 or > 12) throw new ArgumentException("MonthNumber must be 1..12.", nameof(lines));
            var hit = b.Lines.FirstOrDefault(x => x.AccountCode == l.AccountCode && x.MonthNumber == l.MonthNumber);
            if (hit is null)
            {
                b.Lines.Add(new BudgetLine
                {
                    AnnualBudgetId = b.AnnualBudgetId,
                    AccountCode = l.AccountCode, MonthNumber = l.MonthNumber,
                    AmountMinor = l.AmountMinor, CurrencyCode = b.CurrencyCode
                });
            }
            else
            {
                hit.AmountMinor = l.AmountMinor;
            }
        }
        await _db.SaveChangesAsync(ct);
        return b;
    }

    public async Task<AnnualBudget> ApproveAsync(Guid budgetId, Guid actorUserId, CancellationToken ct = default)
    {
        var b = await _db.Budgets.FirstOrDefaultAsync(x => x.AnnualBudgetId == budgetId, ct)
            ?? throw new BudgetingException($"Budget {budgetId} not found.");
        if (b.Status == BudgetStatus.Closed) throw new BudgetingException("Closed; cannot re-approve.");
        b.Status = BudgetStatus.Approved;
        b.ApprovedAt = _clock.GetUtcNow();
        b.ApprovedByUserId = actorUserId;
        await _db.SaveChangesAsync(ct);
        return b;
    }

    public async Task<IReadOnlyList<BudgetVarianceRow>> VarianceAsync(Guid budgetId, CancellationToken ct = default)
    {
        var b = await _db.Budgets.Include(x => x.Lines).FirstOrDefaultAsync(x => x.AnnualBudgetId == budgetId, ct)
            ?? throw new BudgetingException($"Budget {budgetId} not found.");

        // Pull actuals from ledger lines, grouped by (account, month).
        var fromDate = new DateOnly(b.FiscalYear, 1, 1);
        var toDate = new DateOnly(b.FiscalYear, 12, 31);
        var actuals = await _ledger.EventLines
            .AsNoTracking()
            .Where(l => l.Event.TenantId == b.TenantId
                     && l.CurrencyCode == b.CurrencyCode
                     && l.Event.EffectiveDate >= fromDate
                     && l.Event.EffectiveDate <= toDate)
            .GroupBy(l => new { l.AccountCode, l.Event.EffectiveDate.Month })
            .Select(g => new
            {
                g.Key.AccountCode,
                Month = (byte)g.Key.Month,
                ActualMinor = (g.Sum(x => (long?)x.DebitMinor) ?? 0L) - (g.Sum(x => (long?)x.CreditMinor) ?? 0L)
            })
            .ToListAsync(ct);

        var rows = new List<BudgetVarianceRow>(b.Lines.Count);
        foreach (var line in b.Lines.OrderBy(l => l.AccountCode).ThenBy(l => l.MonthNumber))
        {
            var actual = actuals.FirstOrDefault(a => a.AccountCode == line.AccountCode && a.Month == line.MonthNumber)?.ActualMinor ?? 0L;
            var variance = line.AmountMinor - actual;
            var pct = line.AmountMinor == 0 ? 0m : (decimal)variance * 100m / Math.Abs(line.AmountMinor);
            rows.Add(new BudgetVarianceRow(line.AccountCode, line.MonthNumber, line.AmountMinor, actual, variance, pct));
        }
        return rows;
    }

    public async Task<IReadOnlyList<CashForecastRow>> CashForecast13WeeksAsync(DateOnly from, long tenantId = 1, CancellationToken ct = default)
    {
        var weekStart = StartOfWeek(from);
        var weeks = Enumerable.Range(0, 13).Select(i => weekStart.AddDays(i * 7)).ToList();

        // Receivables — sum outstanding (gross - paid) by week of due_date.
        var arDue = _ar is null
            ? new List<DueRow>()
            : (await _ar.Invoices
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId
                         && (i.Status == ArInvoiceStatus.Issued || i.Status == ArInvoiceStatus.PartiallyPaid))
                .Select(i => new DueRow(i.DueDate, i.GrossMinor - i.PaidMinor))
                .ToListAsync(ct));

        // Payables — sum outstanding by week of due_date.
        var apDue = _ap is null
            ? new List<DueRow>()
            : (await _ap.Bills
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId
                         && (i.Status == ApBillStatus.Approved || i.Status == ApBillStatus.PartiallyPaid))
                .Select(i => new DueRow(i.DueDate, i.GrossMinor - i.PaidMinor))
                .ToListAsync(ct));

        var rows = new List<CashForecastRow>(13);
        foreach (var w in weeks)
        {
            var weekEnd = w.AddDays(7);
            var receipts = arDue.Where(x => x.DueDate >= w && x.DueDate < weekEnd).Sum(x => x.OutstandingMinor);
            var payments = apDue.Where(x => x.DueDate >= w && x.DueDate < weekEnd).Sum(x => x.OutstandingMinor);
            rows.Add(new CashForecastRow(w, receipts, payments, receipts - payments));
        }
        return rows;
    }

    private sealed record DueRow(DateOnly DueDate, long OutstandingMinor);

    private static DateOnly StartOfWeek(DateOnly d)
    {
        // Monday-start weeks, ISO style.
        var dayOfWeek = (int)d.DayOfWeek;
        var deltaToMonday = ((dayOfWeek + 6) % 7);
        return d.AddDays(-deltaToMonday);
    }
}
