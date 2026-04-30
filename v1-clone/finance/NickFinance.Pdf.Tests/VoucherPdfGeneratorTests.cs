using System.Text;
using Xunit;

namespace NickFinance.Pdf.Tests;

[Collection("pdf")]
public sealed class VoucherPdfGeneratorTests
{
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");

    private static VoucherPdfModel SampleVoucher(
        string status = "Disbursed",
        string? approverName = "Approver Adjeley",
        string? disbursedByName = "Custodian Kwame") => new(
            VoucherNo: "PC-TEMA-2026-00421",
            Status: status,
            RequesterName: "Requester Akua",
            ApproverName: approverName,
            DisbursedByName: disbursedByName,
            Category: "Fuel",
            Purpose: "Top up generator at the gate during the harmattan power dip.",
            PayeeName: "Star Oil Tema",
            CurrencyCode: "GHS",
            AmountRequestedMinor: 25_000,
            AmountApprovedMinor: 25_000,
            TaxTreatment: "GhanaInclusive",
            WhtTreatment: "GoodsVatRegistered",
            ProjectCode: "OPS-TEMA",
            DisbursementChannel: "cash",
            DisbursementReference: "RECEIPT-44219",
            Lines: new List<VoucherPdfLine>
            {
                new(LineNo: 1, Description: "20L diesel", GlAccount: "6310", GrossAmountMinor: 25_000)
            },
            CreatedAt: new DateTimeOffset(2026, 4, 28, 7, 0, 0, TimeSpan.Zero),
            SubmittedAt: new DateTimeOffset(2026, 4, 28, 7, 5, 0, TimeSpan.Zero),
            DecidedAt: new DateTimeOffset(2026, 4, 28, 8, 0, 0, TimeSpan.Zero),
            DisbursedAt: new DateTimeOffset(2026, 4, 28, 9, 30, 0, TimeSpan.Zero));

    [Fact]
    public void Generate_returns_non_empty_pdf_bytes()
    {
        var gen = new VoucherPdfGenerator();
        var bytes = gen.Generate(SampleVoucher());

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }

    [Fact]
    public void Generate_handles_pending_approval_and_pending_disbursement()
    {
        // A Submitted voucher has no approver / disburser yet — the
        // generator must render those slots as "(pending)" without
        // throwing.
        var gen = new VoucherPdfGenerator();
        var model = SampleVoucher(status: "Submitted", approverName: null, disbursedByName: null) with
        {
            AmountApprovedMinor = null,
            DecidedAt = null,
            DisbursedAt = null,
            DisbursementChannel = null,
            DisbursementReference = null
        };

        var bytes = gen.Generate(model);

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }
}
