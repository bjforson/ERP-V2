using Microsoft.EntityFrameworkCore;

namespace NickFinance.AR;

/// <summary>
/// Identifies overdue AR invoices and produces dunning notices —
/// reminder texts the host can dispatch via NickComms (email + SMS) or
/// surface in the customer portal. The service itself doesn't send;
/// production wires <see cref="IDunningDispatcher"/> to the comms gateway.
/// </summary>
public interface IDunningService
{
    /// <summary>Generate one <see cref="DunningNotice"/> per overdue invoice as of <paramref name="asOf"/>.</summary>
    Task<IReadOnlyList<DunningNotice>> RunAsync(DateOnly asOf, long tenantId = 1, CancellationToken ct = default);
}

public sealed record DunningNotice(
    Guid InvoiceId,
    string InvoiceNo,
    Guid CustomerId,
    string CustomerName,
    string? CustomerEmail,
    string? CustomerPhone,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    int DaysOverdue,
    long OutstandingMinor,
    string CurrencyCode,
    DunningTone Tone,
    string Subject,
    string Body);

public enum DunningTone
{
    /// <summary>0-29 days late — friendly reminder.</summary>
    Friendly = 0,
    /// <summary>30-59 days — firm reminder.</summary>
    Firm = 1,
    /// <summary>60-89 days — escalation note.</summary>
    Final = 2,
    /// <summary>90+ days — collections / legal.</summary>
    Collections = 3
}

public interface IDunningDispatcher
{
    Task DispatchAsync(DunningNotice notice, CancellationToken ct = default);
}

public sealed class DunningService : IDunningService
{
    private readonly ArDbContext _db;
    public DunningService(ArDbContext db) => _db = db;

    public async Task<IReadOnlyList<DunningNotice>> RunAsync(DateOnly asOf, long tenantId = 1, CancellationToken ct = default)
    {
        var open = await _db.Invoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                     && (i.Status == ArInvoiceStatus.Issued || i.Status == ArInvoiceStatus.PartiallyPaid)
                     && i.DueDate < asOf)
            .ToListAsync(ct);

        var customerIds = open.Select(i => i.CustomerId).Distinct().ToList();
        var customers = await _db.Customers
            .AsNoTracking()
            .Where(c => customerIds.Contains(c.CustomerId))
            .ToDictionaryAsync(c => c.CustomerId, c => c, ct);

        var notices = new List<DunningNotice>(open.Count);
        foreach (var i in open)
        {
            customers.TryGetValue(i.CustomerId, out var c);
            var days = asOf.DayNumber - i.DueDate.DayNumber;
            var tone = days switch
            {
                < 30 => DunningTone.Friendly,
                < 60 => DunningTone.Firm,
                < 90 => DunningTone.Final,
                _ => DunningTone.Collections
            };
            var (subject, body) = ComposeMessage(i, c, days, tone);
            notices.Add(new DunningNotice(
                i.ArInvoiceId, i.InvoiceNo, i.CustomerId, c?.Name ?? "(unknown)",
                c?.Email, c?.Phone,
                i.InvoiceDate, i.DueDate, days,
                i.GrossMinor - i.PaidMinor, i.CurrencyCode,
                tone, subject, body));
        }
        return notices.OrderByDescending(n => n.DaysOverdue).ToList();
    }

    private static (string Subject, string Body) ComposeMessage(ArInvoice i, Customer? c, int days, DunningTone tone)
    {
        var amt = ((decimal)(i.GrossMinor - i.PaidMinor) / 100m).ToString("N2");
        var name = c?.Name ?? "Customer";
        return tone switch
        {
            DunningTone.Friendly => (
                $"Reminder: invoice {i.InvoiceNo} due {i.DueDate:yyyy-MM-dd}",
                $"Hi {name},\n\nThis is a friendly reminder that invoice {i.InvoiceNo} for {amt} {i.CurrencyCode} was due on {i.DueDate:yyyy-MM-dd}. Please remit at your earliest convenience.\n\n— Nick TC-Scan accounts"),
            DunningTone.Firm => (
                $"Second reminder: invoice {i.InvoiceNo} now {days} days overdue",
                $"Hi {name},\n\nOur records show invoice {i.InvoiceNo} for {amt} {i.CurrencyCode} is {days} days past due. Please process payment promptly.\n\n— Nick TC-Scan accounts"),
            DunningTone.Final => (
                $"FINAL NOTICE: invoice {i.InvoiceNo} ({days} days overdue)",
                $"Dear {name},\n\nThis is our final notice before collections. Invoice {i.InvoiceNo} for {amt} {i.CurrencyCode} is {days} days overdue. Please clear it within 7 days to avoid escalation.\n\n— Nick TC-Scan accounts"),
            DunningTone.Collections => (
                $"COLLECTIONS NOTICE: invoice {i.InvoiceNo}",
                $"Dear {name},\n\nInvoice {i.InvoiceNo} for {amt} {i.CurrencyCode} has been outstanding for {days} days. We're handing this to our collections team. Please contact accounts immediately.\n\n— Nick TC-Scan accounts"),
            _ => (string.Empty, string.Empty)
        };
    }
}
