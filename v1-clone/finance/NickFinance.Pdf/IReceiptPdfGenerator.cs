namespace NickFinance.Pdf;

/// <summary>
/// Renders an AR receipt acknowledgement (the proof-of-payment slip
/// emitted when a customer settles an invoice) as a PDF byte stream.
/// </summary>
public interface IReceiptPdfGenerator
{
    /// <summary>Render the receipt as PDF bytes.</summary>
    byte[] Generate(ReceiptPdfModel model);
}

/// <summary>View-model for the receipt acknowledgement PDF.</summary>
/// <param name="ReceiptNo">Receipt identifier — typically the receipt's GUID prefix or a generated R-number.</param>
/// <param name="InvoiceNo">The invoice the receipt was applied to.</param>
/// <param name="CustomerName">Customer who paid; printed as "Received from".</param>
/// <param name="CurrencyCode">ISO 4217 currency.</param>
/// <param name="AmountMinor">Receipt amount in minor units.</param>
/// <param name="ReceivedAt">Receipt date.</param>
/// <param name="PaymentMethod">Free-text channel description, e.g. <c>Bank transfer (GCB)</c> or <c>MoMo MTN</c>.</param>
/// <param name="Reference">Bank / MoMo reference for audit.</param>
/// <param name="InvoiceOutstandingMinor">Outstanding balance on the invoice AFTER this receipt — null if not computed.</param>
public sealed record ReceiptPdfModel(
    string ReceiptNo,
    string InvoiceNo,
    string CustomerName,
    string CurrencyCode,
    long AmountMinor,
    DateOnly ReceivedAt,
    string PaymentMethod,
    string? Reference,
    long? InvoiceOutstandingMinor);
