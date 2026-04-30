using System.Text;
using Xunit;

namespace NickFinance.Pdf.Tests;

[Collection("pdf")]
public sealed class ReceiptPdfGeneratorTests
{
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");

    private static ReceiptPdfModel Sample(long? outstanding = 0L) => new(
        ReceiptNo: "R-A1B2C3D4",
        InvoiceNo: "INV-2026-04-00001",
        CustomerName: "Agro Imports Ltd",
        CurrencyCode: "GHS",
        AmountMinor: 123_625,
        ReceivedAt: new DateOnly(2026, 4, 28),
        PaymentMethod: "Bank GCB",
        Reference: "GCB-TXN-9981234",
        InvoiceOutstandingMinor: outstanding);

    [Fact]
    public void Generate_returns_non_empty_pdf_bytes()
    {
        var gen = new ReceiptPdfGenerator();
        var bytes = gen.Generate(Sample());

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }

    [Fact]
    public void Generate_renders_when_outstanding_is_unknown()
    {
        // A null outstanding balance is the "we didn't compute it" case;
        // the generator should skip the outstanding-balance line entirely
        // rather than render "null" or zero.
        var gen = new ReceiptPdfGenerator();
        var bytes = gen.Generate(Sample(outstanding: null));

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }
}
