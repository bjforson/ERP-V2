namespace NickFinance.Pdf;

/// <summary>
/// Renders an AR invoice as a PDF byte stream. The model is fully
/// pre-resolved (customer name, line items, etc.) so the generator stays
/// pure — no DbContext, no <see cref="HttpContext"/>, no clock — which
/// keeps it trivially unit-testable.
/// </summary>
public interface IInvoicePdfGenerator
{
    /// <summary>
    /// Render the invoice as PDF bytes. Includes Nick TC-Scan branding,
    /// customer block, line items, levies + VAT breakdown, IRN + QR if
    /// present, payment terms, and the standard footer.
    /// </summary>
    byte[] Generate(InvoicePdfModel model);
}

/// <summary>
/// View-model for the invoice PDF. Built from <c>ArInvoice</c> + lines +
/// <c>Customer</c> on the endpoint side; carries everything the generator
/// needs and nothing it doesn't.
/// </summary>
/// <param name="InvoiceNo">Human-friendly invoice number, e.g. <c>INV-2026-04-00001</c>. Empty string for unissued.</param>
/// <param name="InvoiceDate">Invoice date (the date taxes were computed).</param>
/// <param name="DueDate">Optional due date — if null the PDF prints "On receipt".</param>
/// <param name="CustomerName">Display name for the bill-to block.</param>
/// <param name="CustomerTin">Customer Tax Identification Number; printed when present.</param>
/// <param name="CustomerAddress">Free-form mailing address; printed verbatim.</param>
/// <param name="CurrencyCode">ISO 4217 currency, used as suffix on every money figure.</param>
/// <param name="SubtotalNetMinor">Sum of line nets, in minor units.</param>
/// <param name="LeviesMinor">Total Ghana levies (NHIL + GETFund + COVID), minor units.</param>
/// <param name="VatMinor">VAT, minor units.</param>
/// <param name="GrossMinor">Gross total, minor units.</param>
/// <param name="EvatIrn">GRA e-VAT IRN. Null for unissued invoices.</param>
/// <param name="IrnIsSandbox">When true the PDF tags the document SANDBOX (don't bill the customer).</param>
/// <param name="Lines">Line items in display order.</param>
/// <param name="Reference">Optional reference / customer PO / scan declaration number.</param>
/// <param name="Notes">Optional free-text notes printed below the totals.</param>
public sealed record InvoicePdfModel(
    string InvoiceNo,
    DateOnly InvoiceDate,
    DateOnly? DueDate,
    string CustomerName,
    string? CustomerTin,
    string? CustomerAddress,
    string CurrencyCode,
    long SubtotalNetMinor,
    long LeviesMinor,
    long VatMinor,
    long GrossMinor,
    string? EvatIrn,
    bool IrnIsSandbox,
    IReadOnlyList<InvoicePdfLine> Lines,
    string? Reference,
    string? Notes);

/// <summary>One row in the line-items table.</summary>
/// <param name="LineNo">1-based ordinal.</param>
/// <param name="Description">Free-text description.</param>
/// <param name="Quantity">Quantity (decimal — supports fractions).</param>
/// <param name="UnitPriceMinor">Unit price in minor units (typically <c>LineTotalMinor / Quantity</c>; passed through verbatim for display).</param>
/// <param name="LineTotalMinor">Line net total in minor units.</param>
public sealed record InvoicePdfLine(
    int LineNo,
    string Description,
    decimal Quantity,
    long UnitPriceMinor,
    long LineTotalMinor);
