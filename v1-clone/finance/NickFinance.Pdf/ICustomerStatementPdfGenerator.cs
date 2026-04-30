namespace NickFinance.Pdf;

/// <summary>
/// Renders a per-customer statement of account as a PDF byte stream.
/// Mirrors the on-screen statement page on <c>/ar/statement</c>: customer
/// header, opening balance, list of invoices + receipts in date order,
/// closing balance, and an ageing summary table at the foot.
/// </summary>
public interface ICustomerStatementPdfGenerator
{
    /// <summary>Render the statement as PDF bytes.</summary>
    byte[] Generate(CustomerStatementPdfModel model);
}

/// <summary>View-model for a customer statement PDF. Built from the
/// <c>CustomerStatement</c> aggregate on the endpoint side; the generator
/// stays pure / unit-testable.</summary>
/// <param name="CustomerName">Customer display name.</param>
/// <param name="CustomerTin">TIN, printed when present.</param>
/// <param name="CustomerAddress">Free-form mailing address.</param>
/// <param name="PeriodFrom">Inclusive start of the statement window.</param>
/// <param name="PeriodTo">Inclusive end of the statement window.</param>
/// <param name="CurrencyCode">ISO 4217 currency code, used as suffix on every money figure.</param>
/// <param name="OpeningBalanceMinor">Balance as of the day before <paramref name="PeriodFrom"/>, in minor units.</param>
/// <param name="ClosingBalanceMinor">Balance at end of <paramref name="PeriodTo"/>, in minor units.</param>
/// <param name="Lines">Statement lines in chronological order.</param>
/// <param name="Ageing">Optional ageing buckets at the statement date — null hides the ageing table.</param>
public sealed record CustomerStatementPdfModel(
    string CustomerName,
    string? CustomerTin,
    string? CustomerAddress,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    string CurrencyCode,
    long OpeningBalanceMinor,
    long ClosingBalanceMinor,
    IReadOnlyList<StatementLine> Lines,
    AgeingSummary? Ageing = null);

/// <summary>One row in the statement ledger.</summary>
/// <param name="Date">The date of the underlying invoice / receipt / adjustment.</param>
/// <param name="DocumentType">"Invoice", "Receipt", "Adjustment", or "Opening balance".</param>
/// <param name="DocumentRef">e.g. "INV-2026-04-00012".</param>
/// <param name="DebitMinor">Positive = customer owes more (typically an invoice).</param>
/// <param name="CreditMinor">Positive = customer paid (typically a receipt).</param>
/// <param name="RunningBalanceMinor">Running balance after this line, in minor units.</param>
public sealed record StatementLine(
    DateOnly Date,
    string DocumentType,
    string DocumentRef,
    long DebitMinor,
    long CreditMinor,
    long RunningBalanceMinor);

/// <summary>Standard ageing buckets in minor units. The implementation
/// computes these against the closing balance and individual invoice
/// outstanding amounts; for v1 they're optional on the model so a caller
/// can omit them while we wire that calc on the AR side.</summary>
/// <param name="CurrentMinor">0–30 days since invoice date.</param>
/// <param name="Days30Minor">31–60 days.</param>
/// <param name="Days60Minor">61–90 days.</param>
/// <param name="Days90Minor">91–120 days.</param>
/// <param name="Days120PlusMinor">121+ days.</param>
public sealed record AgeingSummary(
    long CurrentMinor,
    long Days30Minor,
    long Days60Minor,
    long Days90Minor,
    long Days120PlusMinor);
