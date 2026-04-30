namespace NickFinance.Pdf;

/// <summary>
/// Renders one or more Ghana WHT certificates as a single PDF. The
/// year-end book is a cover-less concatenation: each vendor gets a fresh
/// page, identical layout, with a hard page break in between. A
/// single-vendor request is just a one-element list.
/// </summary>
public interface IWhtCertificatePdfGenerator
{
    /// <summary>Render the certificates as PDF bytes. Caller must pass at
    /// least one element; an empty list is rejected.</summary>
    byte[] Generate(IReadOnlyList<WhtCertificatePdfModel> certificates);
}

/// <summary>View-model for one vendor's annual WHT certificate.</summary>
/// <param name="VendorName">Vendor display name.</param>
/// <param name="VendorTin">Vendor's Ghana Card PIN / TIN.</param>
/// <param name="Year">Calendar year covered.</param>
/// <param name="Payments">Per-payment breakdown.</param>
/// <param name="TotalGrossMinor">Sum of all gross amounts paid, minor units.</param>
/// <param name="TotalWhtMinor">Sum of all WHT deducted, minor units.</param>
/// <param name="CurrencyCode">ISO 4217 currency.</param>
public sealed record WhtCertificatePdfModel(
    string VendorName,
    string? VendorTin,
    int Year,
    IReadOnlyList<WhtCertificatePaymentLine> Payments,
    long TotalGrossMinor,
    long TotalWhtMinor,
    string CurrencyCode);

/// <summary>One row in a WHT certificate's payment table.</summary>
/// <param name="PaymentDate">Date the payment was recorded.</param>
/// <param name="PaymentRef">Rail / cheque / cert reference.</param>
/// <param name="InvoiceNo">Vendor invoice / bill number, when available.</param>
/// <param name="GrossMinor">Gross amount paid in minor units.</param>
/// <param name="WhtRatePct">Effective WHT rate in percent.</param>
/// <param name="WhtMinor">WHT deducted in minor units.</param>
public sealed record WhtCertificatePaymentLine(
    DateOnly PaymentDate,
    string PaymentRef,
    string? InvoiceNo,
    long GrossMinor,
    decimal WhtRatePct,
    long WhtMinor);
