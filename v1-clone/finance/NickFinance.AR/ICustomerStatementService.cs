using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace NickFinance.AR;

/// <summary>
/// Customer statement = ledger of one customer's invoices + receipts
/// over a date range, with a running balance. v1 emits CSV + plain
/// text; PDF generation comes when QuestPDF lands as a dependency.
/// </summary>
public interface ICustomerStatementService
{
    Task<CustomerStatement> BuildAsync(Guid customerId, DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default);
    Task<byte[]> RenderCsvAsync(CustomerStatement statement);

    /// <summary>
    /// Compute the standard 0/30/60/90/120+ ageing buckets for a customer
    /// as of <paramref name="asOf"/>, using each invoice's outstanding
    /// balance and its invoice date as the age anchor. Returned tuple is
    /// in minor units, in the customer's billing currency.
    /// </summary>
    Task<(long CurrentMinor, long Days30Minor, long Days60Minor, long Days90Minor, long Days120PlusMinor)>
        ComputeAgeingAsync(Guid customerId, DateOnly asOf, long tenantId = 1, CancellationToken ct = default);
}

public sealed record CustomerStatement(
    Customer Customer,
    DateOnly From,
    DateOnly To,
    string CurrencyCode,
    long OpeningBalanceMinor,
    long ClosingBalanceMinor,
    IReadOnlyList<StatementRow> Rows);

public sealed record StatementRow(
    DateOnly Date,
    string Type,         // "Invoice" or "Receipt"
    string Reference,    // invoice no / receipt rail ref
    string? Description,
    long DebitMinor,     // invoice = debit
    long CreditMinor,    // receipt = credit
    long RunningBalanceMinor);

public sealed class CustomerStatementService : ICustomerStatementService
{
    private readonly ArDbContext _db;
    public CustomerStatementService(ArDbContext db) => _db = db;

    public async Task<CustomerStatement> BuildAsync(Guid customerId, DateOnly from, DateOnly to, long tenantId = 1, CancellationToken ct = default)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == customerId && c.TenantId == tenantId, ct)
            ?? throw new ArException($"Customer {customerId} not found.");

        // Opening = sum debits before `from` minus sum credits before `from`.
        var pre = await SumAsync(tenantId, customerId, null, from.AddDays(-1), ct);
        var rangeInv = await _db.Invoices.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.CustomerId == customerId
                     && i.Status != ArInvoiceStatus.Draft && i.Status != ArInvoiceStatus.Void
                     && i.InvoiceDate >= from && i.InvoiceDate <= to)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.InvoiceNo)
            .ToListAsync(ct);
        var rangeReceipts = await _db.Receipts.AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && _db.Invoices.Any(i => i.ArInvoiceId == r.ArInvoiceId && i.CustomerId == customerId)
                     && r.ReceiptDate >= from && r.ReceiptDate <= to)
            .OrderBy(r => r.ReceiptDate)
            .ToListAsync(ct);

        var rows = new List<StatementRow>();
        var running = pre;
        foreach (var item in rangeInv.Cast<object>().Concat(rangeReceipts).OrderBy(o => o switch
        {
            ArInvoice i => i.InvoiceDate.ToDateTime(TimeOnly.MinValue),
            ArReceipt r => r.ReceiptDate.ToDateTime(TimeOnly.MinValue),
            _ => DateTime.MinValue
        }))
        {
            switch (item)
            {
                case ArInvoice inv:
                    running += inv.GrossMinor;
                    rows.Add(new StatementRow(inv.InvoiceDate, "Invoice", inv.InvoiceNo, inv.Reference,
                        inv.GrossMinor, 0, running));
                    break;
                case ArReceipt rec:
                    running -= rec.AmountMinor;
                    rows.Add(new StatementRow(rec.ReceiptDate, "Receipt", rec.Reference ?? "(no ref)",
                        $"Payment via {rec.CashAccount}", 0, rec.AmountMinor, running));
                    break;
            }
        }
        return new CustomerStatement(customer, from, to, customer.IsVatRegistered ? "GHS" : "GHS",
            pre, running, rows);
    }

    private async Task<long> SumAsync(long tenantId, Guid customerId, DateOnly? from, DateOnly to, CancellationToken ct)
    {
        var inv = await _db.Invoices.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.CustomerId == customerId
                     && i.Status != ArInvoiceStatus.Draft && i.Status != ArInvoiceStatus.Void
                     && (from == null || i.InvoiceDate >= from) && i.InvoiceDate <= to)
            .SumAsync(i => (long?)i.GrossMinor, ct) ?? 0;
        var rec = await _db.Receipts.AsNoTracking()
            .Where(r => r.TenantId == tenantId
                     && _db.Invoices.Any(i => i.ArInvoiceId == r.ArInvoiceId && i.CustomerId == customerId)
                     && (from == null || r.ReceiptDate >= from) && r.ReceiptDate <= to)
            .SumAsync(r => (long?)r.AmountMinor, ct) ?? 0;
        return inv - rec;
    }

    public Task<byte[]> RenderCsvAsync(CustomerStatement s)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Customer,{Quote(s.Customer.Name)},Code,{Quote(s.Customer.Code)}");
        sb.AppendLine($"Period,{s.From:yyyy-MM-dd},{s.To:yyyy-MM-dd},Currency,{s.CurrencyCode}");
        sb.AppendLine($"OpeningBalance,{Money(s.OpeningBalanceMinor)}");
        sb.AppendLine();
        sb.AppendLine("Date,Type,Reference,Description,Debit,Credit,RunningBalance");
        foreach (var r in s.Rows)
        {
            sb.Append(r.Date.ToString("yyyy-MM-dd")).Append(',');
            sb.Append(r.Type).Append(',');
            sb.Append(Quote(r.Reference)).Append(',');
            sb.Append(Quote(r.Description ?? string.Empty)).Append(',');
            sb.Append(Money(r.DebitMinor)).Append(',');
            sb.Append(Money(r.CreditMinor)).Append(',');
            sb.AppendLine(Money(r.RunningBalanceMinor));
        }
        sb.AppendLine();
        sb.AppendLine($"ClosingBalance,{Money(s.ClosingBalanceMinor)}");
        return Task.FromResult(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static string Money(long minor) => ((decimal)minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    private static string Quote(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    /// <inheritdoc />
    public async Task<(long CurrentMinor, long Days30Minor, long Days60Minor, long Days90Minor, long Days120PlusMinor)>
        ComputeAgeingAsync(Guid customerId, DateOnly asOf, long tenantId = 1, CancellationToken ct = default)
    {
        // Pull every non-void / non-draft invoice for the customer; the
        // outstanding-minor we care about is gross less paid as of `asOf`.
        // Receipts dated AFTER `asOf` shouldn't reduce the bucket — we
        // recompute paid-as-of-date by clamping receipts to that date.
        var invoices = await _db.Invoices.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.CustomerId == customerId
                     && i.Status != ArInvoiceStatus.Draft && i.Status != ArInvoiceStatus.Void
                     && i.InvoiceDate <= asOf)
            .Select(i => new { i.ArInvoiceId, i.InvoiceDate, i.GrossMinor })
            .ToListAsync(ct);
        if (invoices.Count == 0) return (0, 0, 0, 0, 0);

        var ids = invoices.Select(i => i.ArInvoiceId).ToList();
        var paid = await _db.Receipts.AsNoTracking()
            .Where(r => r.TenantId == tenantId && ids.Contains(r.ArInvoiceId) && r.ReceiptDate <= asOf)
            .GroupBy(r => r.ArInvoiceId)
            .Select(g => new { Id = g.Key, Paid = g.Sum(r => (long?)r.AmountMinor) ?? 0L })
            .ToDictionaryAsync(x => x.Id, x => x.Paid, ct);

        long b0 = 0, b30 = 0, b60 = 0, b90 = 0, b120 = 0;
        foreach (var inv in invoices)
        {
            paid.TryGetValue(inv.ArInvoiceId, out var p);
            var outstanding = inv.GrossMinor - p;
            if (outstanding <= 0) continue;
            var days = asOf.DayNumber - inv.InvoiceDate.DayNumber;
            if      (days <= 30)  b0 += outstanding;
            else if (days <= 60)  b30 += outstanding;
            else if (days <= 90)  b60 += outstanding;
            else if (days <= 120) b90 += outstanding;
            else                  b120 += outstanding;
        }
        return (b0, b30, b60, b90, b120);
    }
}
