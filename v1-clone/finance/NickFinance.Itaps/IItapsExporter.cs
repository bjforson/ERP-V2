using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NickFinance.AP;
using NickFinance.AR;

namespace NickFinance.Itaps;

/// <summary>
/// Generates GRA-acceptable CSV schedules for the monthly VAT return,
/// the monthly WHT return, and the e-SSNIT contribution schedule. The
/// shapes match the iTaPS template formats; production deployment may
/// add a per-tenant transform if GRA tweaks the column order.
/// </summary>
public interface IItapsExporter
{
    /// <summary>VAT return — one row per AR invoice issued in the period, with a totals tail.</summary>
    Task<byte[]> VatReturnCsvAsync(int year, int month, long tenantId = 1, CancellationToken ct = default);

    /// <summary>WHT return — one row per AP-issued WHT certificate in the period.</summary>
    Task<byte[]> WhtReturnCsvAsync(int year, int month, long tenantId = 1, CancellationToken ct = default);

    /// <summary>SSNIT contribution schedule — one row per employee × period. Sourced from a host-supplied <see cref="ISsnitFeed"/>.</summary>
    Task<byte[]> SsnitScheduleCsvAsync(int year, int month, ISsnitFeed feed, long tenantId = 1, CancellationToken ct = default);
}

/// <summary>Pluggable feed for the SSNIT export — production wires NickHR's payroll module.</summary>
public interface ISsnitFeed
{
    Task<IReadOnlyList<SsnitRow>> GetMonthAsync(int year, int month, long tenantId, CancellationToken ct);
}

public sealed record SsnitRow(
    string SsnitNumber,
    string FullName,
    long GrossPayMinor,
    long Tier1EmployeeMinor,
    long Tier1EmployerMinor,
    long Tier2EmployerMinor);

public sealed class ItapsExporter : IItapsExporter
{
    private readonly ArDbContext? _ar;
    private readonly ApDbContext? _ap;

    public ItapsExporter(ArDbContext? ar, ApDbContext? ap)
    {
        _ar = ar;
        _ap = ap;
    }

    public async Task<byte[]> VatReturnCsvAsync(int year, int month, long tenantId = 1, CancellationToken ct = default)
    {
        if (_ar is null) return Encoding.UTF8.GetBytes(Header(VatColumns) + "\n");
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var invoices = await _ar.Invoices
            .AsNoTracking()
            .Where(i => i.TenantId == tenantId
                     && i.Status != ArInvoiceStatus.Draft
                     && i.Status != ArInvoiceStatus.Void
                     && i.InvoiceDate >= from && i.InvoiceDate <= to)
            .OrderBy(i => i.InvoiceNo)
            .ToListAsync(ct);
        var customers = await _ar.Customers
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .ToDictionaryAsync(c => c.CustomerId, c => c, ct);

        var sb = new StringBuilder();
        sb.AppendLine(Header(VatColumns));
        long totalNet = 0, totalLevies = 0, totalVat = 0, totalGross = 0;
        foreach (var i in invoices)
        {
            customers.TryGetValue(i.CustomerId, out var c);
            sb.Append(Quote(i.InvoiceNo)).Append(',');
            sb.Append(i.InvoiceDate.ToString("yyyy-MM-dd")).Append(',');
            sb.Append(Quote(c?.Name ?? "(unknown)")).Append(',');
            sb.Append(Quote(c?.Tin ?? string.Empty)).Append(',');
            sb.Append(Quote(i.EvatIrn ?? string.Empty)).Append(',');
            sb.Append(Money(i.SubtotalNetMinor)).Append(',');
            sb.Append(Money(i.LeviesMinor)).Append(',');
            sb.Append(Money(i.VatMinor)).Append(',');
            sb.Append(Money(i.GrossMinor)).Append(',');
            sb.AppendLine(i.CurrencyCode);
            totalNet += i.SubtotalNetMinor; totalLevies += i.LeviesMinor;
            totalVat += i.VatMinor; totalGross += i.GrossMinor;
        }
        sb.AppendLine($"TOTAL,,,,, {Money(totalNet)},{Money(totalLevies)},{Money(totalVat)},{Money(totalGross)},");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<byte[]> WhtReturnCsvAsync(int year, int month, long tenantId = 1, CancellationToken ct = default)
    {
        if (_ap is null) return Encoding.UTF8.GetBytes(Header(WhtColumns) + "\n");
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var certs = await _ap.WhtCertificates
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IssueDate >= from && c.IssueDate <= to)
            .OrderBy(c => c.CertificateNo)
            .ToListAsync(ct);
        var vendors = await _ap.Vendors
            .AsNoTracking()
            .Where(v => v.TenantId == tenantId)
            .ToDictionaryAsync(v => v.VendorId, v => v, ct);

        var sb = new StringBuilder();
        sb.AppendLine(Header(WhtColumns));
        long totalGross = 0, totalWht = 0;
        foreach (var c in certs)
        {
            vendors.TryGetValue(c.VendorId, out var v);
            sb.Append(Quote(c.CertificateNo)).Append(',');
            sb.Append(c.IssueDate.ToString("yyyy-MM-dd")).Append(',');
            sb.Append(Quote(v?.Name ?? "(unknown)")).Append(',');
            sb.Append(Quote(v?.Tin ?? string.Empty)).Append(',');
            sb.Append(c.TransactionType).Append(',');
            sb.Append(c.WhtRate.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Money(c.GrossPaidMinor)).Append(',');
            sb.Append(Money(c.WhtDeductedMinor)).Append(',');
            sb.AppendLine(c.CurrencyCode);
            totalGross += c.GrossPaidMinor; totalWht += c.WhtDeductedMinor;
        }
        sb.AppendLine($"TOTAL,,,,, ,{Money(totalGross)},{Money(totalWht)},");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<byte[]> SsnitScheduleCsvAsync(int year, int month, ISsnitFeed feed, long tenantId = 1, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        var rows = await feed.GetMonthAsync(year, month, tenantId, ct);
        var sb = new StringBuilder();
        sb.AppendLine(Header(SsnitColumns));
        long totalGross = 0, totalT1Ee = 0, totalT1Er = 0, totalT2 = 0;
        foreach (var r in rows)
        {
            sb.Append(Quote(r.SsnitNumber)).Append(',');
            sb.Append(Quote(r.FullName)).Append(',');
            sb.Append(Money(r.GrossPayMinor)).Append(',');
            sb.Append(Money(r.Tier1EmployeeMinor)).Append(',');
            sb.Append(Money(r.Tier1EmployerMinor)).Append(',');
            sb.AppendLine(Money(r.Tier2EmployerMinor));
            totalGross += r.GrossPayMinor; totalT1Ee += r.Tier1EmployeeMinor;
            totalT1Er += r.Tier1EmployerMinor; totalT2 += r.Tier2EmployerMinor;
        }
        sb.AppendLine($"TOTAL,,{Money(totalGross)},{Money(totalT1Ee)},{Money(totalT1Er)},{Money(totalT2)}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    // -----------------------------------------------------------------
    // CSV helpers
    // -----------------------------------------------------------------

    private static readonly string[] VatColumns =
        { "InvoiceNo", "InvoiceDate", "CustomerName", "CustomerTin", "EvatIrn", "Net", "Levies", "Vat", "Gross", "Currency" };
    private static readonly string[] WhtColumns =
        { "CertificateNo", "IssueDate", "VendorName", "VendorTin", "TransactionType", "WhtRate", "GrossPaid", "WhtDeducted", "Currency" };
    private static readonly string[] SsnitColumns =
        { "SsnitNumber", "FullName", "GrossPay", "Tier1Employee", "Tier1Employer", "Tier2Employer" };

    private static string Header(string[] cols) => string.Join(',', cols);
    private static string Money(long minor) => ((decimal)minor / 100m).ToString("0.00", CultureInfo.InvariantCulture);
    private static string Quote(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;
}
