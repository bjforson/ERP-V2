using Microsoft.EntityFrameworkCore;

namespace NickFinance.AP;

/// <summary>
/// Aggregates WHT data for the year-end certificate book. The Ghana
/// statutory requirement is one certificate per vendor showing every
/// payment + WHT deducted for the calendar year, so the buyer's books
/// match the vendor's declaration to GRA.
/// </summary>
public interface IWhtCertificateService
{
    /// <summary>
    /// Return one row per vendor that had any WHT activity during
    /// <paramref name="year"/>. Vendors with zero WHT (e.g. exempt
    /// suppliers) are filtered out — the certificate book only carries
    /// rows worth submitting.
    /// </summary>
    Task<IReadOnlyList<WhtCertificateData>> GetForYearAsync(int year, long tenantId, CancellationToken ct = default);
}

/// <summary>One vendor's WHT picture for a year.</summary>
/// <param name="VendorId">Source vendor id.</param>
/// <param name="VendorName">Vendor display name.</param>
/// <param name="VendorTin">Vendor TIN — printed on the certificate.</param>
/// <param name="Year">Calendar year covered.</param>
/// <param name="Payments">Per-payment line items.</param>
/// <param name="TotalGrossMinor">Sum of <see cref="WhtPayment.GrossMinor"/>.</param>
/// <param name="TotalWhtMinor">Sum of <see cref="WhtPayment.WhtMinor"/>.</param>
public sealed record WhtCertificateData(
    Guid VendorId,
    string VendorName,
    string? VendorTin,
    int Year,
    IReadOnlyList<WhtPayment> Payments,
    long TotalGrossMinor,
    long TotalWhtMinor);

/// <summary>One row in a vendor's certificate.</summary>
/// <param name="Date">Date the payment was recorded.</param>
/// <param name="PaymentRef">Rail / cheque / MoMo reference.</param>
/// <param name="InvoiceNo">Vendor's invoice / bill number, when available.</param>
/// <param name="GrossMinor">Gross amount paid, in minor units.</param>
/// <param name="WhtRatePct">Effective WHT rate in percent (e.g. 7.5 for services).</param>
/// <param name="WhtMinor">WHT deducted in minor units.</param>
public sealed record WhtPayment(
    DateOnly Date,
    string PaymentRef,
    string? InvoiceNo,
    long GrossMinor,
    decimal WhtRatePct,
    long WhtMinor);

/// <summary>
/// EF Core implementation. Drives the aggregation off the
/// <see cref="ApPayment"/> + <see cref="WhtCertificate"/> tables — every
/// payment that produced a WHT certificate row is part of the year.
/// </summary>
public sealed class WhtCertificateService : IWhtCertificateService
{
    private readonly ApDbContext _db;
    public WhtCertificateService(ApDbContext db) => _db = db;

    /// <inheritdoc />
    public async Task<IReadOnlyList<WhtCertificateData>> GetForYearAsync(int year, long tenantId, CancellationToken ct = default)
    {
        // Bound the year to a closed date range; Ghana tax year = calendar year.
        var from = new DateOnly(year, 1, 1);
        var to = new DateOnly(year, 12, 31);

        // Pull every certificate row for the year; left-joined to its
        // payment + bill so we can read the bill no for the cert line.
        // The cert table is the canonical source of "WHT actually withheld"
        // — payments that didn't trigger a cert (exempt vendor, zero
        // amount) should not surface here.
        var certs = await _db.WhtCertificates.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IssueDate >= from && c.IssueDate <= to && c.WhtDeductedMinor > 0)
            .ToListAsync(ct);

        if (certs.Count == 0) return Array.Empty<WhtCertificateData>();

        // Vendor + bill lookups — single round-trip each.
        var vendorIds = certs.Select(c => c.VendorId).Distinct().ToList();
        var vendors = await _db.Vendors.AsNoTracking()
            .Where(v => vendorIds.Contains(v.VendorId))
            .ToDictionaryAsync(v => v.VendorId, ct);

        var billIds = certs.Where(c => c.ApBillId.HasValue).Select(c => c.ApBillId!.Value).Distinct().ToList();
        var bills = billIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Bills.AsNoTracking()
                .Where(b => billIds.Contains(b.ApBillId))
                .ToDictionaryAsync(b => b.ApBillId, b => b.BillNo, ct);

        var paymentIds = certs.Where(c => c.ApPaymentId.HasValue).Select(c => c.ApPaymentId!.Value).Distinct().ToList();
        var payments = paymentIds.Count == 0
            ? new Dictionary<Guid, ApPayment>()
            : await _db.Payments.AsNoTracking()
                .Where(p => paymentIds.Contains(p.ApPaymentId))
                .ToDictionaryAsync(p => p.ApPaymentId, ct);

        // Group + project per vendor.
        var grouped = certs
            .GroupBy(c => c.VendorId)
            .Select(g =>
            {
                vendors.TryGetValue(g.Key, out var v);
                var rows = g
                    .OrderBy(c => c.IssueDate)
                    .Select(c =>
                    {
                        var billNo = c.ApBillId.HasValue && bills.TryGetValue(c.ApBillId.Value, out var b) ? b : null;
                        var paymentRef = c.ApPaymentId.HasValue && payments.TryGetValue(c.ApPaymentId.Value, out var p)
                            ? (p.RailReference ?? p.PaymentRail)
                            : c.CertificateNo;
                        return new WhtPayment(
                            Date: c.IssueDate,
                            PaymentRef: paymentRef,
                            InvoiceNo: billNo,
                            GrossMinor: c.GrossPaidMinor,
                            WhtRatePct: c.WhtRate * 100m,
                            WhtMinor: c.WhtDeductedMinor);
                    })
                    .ToList();

                return new WhtCertificateData(
                    VendorId: g.Key,
                    VendorName: v?.Name ?? "(unknown vendor)",
                    VendorTin: v?.Tin,
                    Year: year,
                    Payments: rows,
                    TotalGrossMinor: rows.Sum(r => r.GrossMinor),
                    TotalWhtMinor: rows.Sum(r => r.WhtMinor));
            })
            .OrderBy(d => d.VendorName)
            .ToList();

        return grouped;
    }
}
