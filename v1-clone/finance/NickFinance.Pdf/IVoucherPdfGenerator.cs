namespace NickFinance.Pdf;

/// <summary>
/// Renders a petty-cash voucher as a PDF byte stream. Used both for the
/// requester to print a payment slip the payee can sign, and for the
/// auditor's "show me the voucher" trail.
/// </summary>
public interface IVoucherPdfGenerator
{
    /// <summary>Render the voucher as PDF bytes. Includes requester +
    /// approver attribution, the line item table, the tax/WHT classification,
    /// and the disbursement timestamp when present.</summary>
    byte[] Generate(VoucherPdfModel model);
}

/// <summary>View-model for a voucher PDF.</summary>
/// <param name="VoucherNo">e.g. <c>PC-TEMA-2026-00421</c>.</param>
/// <param name="Status">String form of <c>VoucherStatus</c>.</param>
/// <param name="RequesterName">Display name of the user who raised the voucher.</param>
/// <param name="ApproverName">Display name of the approver — null until decided.</param>
/// <param name="DisbursedByName">Display name of the custodian who paid out — null until disbursed.</param>
/// <param name="Category">String form of <c>VoucherCategory</c>.</param>
/// <param name="Purpose">Free-text business justification.</param>
/// <param name="PayeeName">Optional 3rd-party payee.</param>
/// <param name="CurrencyCode">ISO 4217 currency.</param>
/// <param name="AmountRequestedMinor">Originally-submitted amount (minor units).</param>
/// <param name="AmountApprovedMinor">Approved amount (minor units) — null until approved.</param>
/// <param name="TaxTreatment">String form of <c>TaxTreatment</c>.</param>
/// <param name="WhtTreatment">String form of <c>WhtTreatment</c>.</param>
/// <param name="ProjectCode">Optional project / cost-centre code.</param>
/// <param name="DisbursementChannel">e.g. <c>cash</c> / <c>momo:hubtel</c>.</param>
/// <param name="DisbursementReference">Rail reference id.</param>
/// <param name="Lines">Voucher line items.</param>
/// <param name="CreatedAt">Voucher creation timestamp.</param>
/// <param name="SubmittedAt">Submission timestamp (null if still draft).</param>
/// <param name="DecidedAt">Approve/Reject timestamp.</param>
/// <param name="DisbursedAt">Disbursement timestamp.</param>
public sealed record VoucherPdfModel(
    string VoucherNo,
    string Status,
    string RequesterName,
    string? ApproverName,
    string? DisbursedByName,
    string Category,
    string Purpose,
    string? PayeeName,
    string CurrencyCode,
    long AmountRequestedMinor,
    long? AmountApprovedMinor,
    string TaxTreatment,
    string WhtTreatment,
    string? ProjectCode,
    string? DisbursementChannel,
    string? DisbursementReference,
    IReadOnlyList<VoucherPdfLine> Lines,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? DisbursedAt);

/// <summary>One row in a voucher's line-item table.</summary>
/// <param name="LineNo">1-based ordinal within the voucher.</param>
/// <param name="Description">Free-text description.</param>
/// <param name="GlAccount">Chart-of-accounts code this line books against.</param>
/// <param name="GrossAmountMinor">Gross amount in minor units.</param>
public sealed record VoucherPdfLine(
    int LineNo,
    string Description,
    string GlAccount,
    long GrossAmountMinor);
