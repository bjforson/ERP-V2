using System.Text;
using Xunit;

namespace NickFinance.Pdf.Tests;

[Collection("pdf")]
public sealed class InvoicePdfGeneratorTests
{
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");

    private static InvoicePdfModel SampleInvoice(
        string? irn = "IRN-2026-04-00001",
        bool isSandbox = false,
        IReadOnlyList<InvoicePdfLine>? lines = null) => new(
            InvoiceNo: "INV-2026-04-00001",
            InvoiceDate: new DateOnly(2026, 4, 28),
            DueDate: new DateOnly(2026, 5, 28),
            CustomerName: "Agro Imports Ltd",
            CustomerTin: "C0012345678",
            CustomerAddress: "12 Harbour Rd, Tema, Greater Accra",
            CurrencyCode: "GHS",
            SubtotalNetMinor: 100_000,
            LeviesMinor: 7_500,
            VatMinor: 16_125,
            GrossMinor: 123_625,
            EvatIrn: irn,
            IrnIsSandbox: isSandbox,
            Lines: lines ?? new List<InvoicePdfLine>
            {
                new(LineNo: 1, Description: "Container scan — TEMA-2026-0421", Quantity: 1m, UnitPriceMinor: 100_000, LineTotalMinor: 100_000)
            },
            Reference: "PO-AGRO-2026-09",
            Notes: null);

    [Fact]
    public void Generate_returns_non_empty_bytes_starting_with_pdf_magic()
    {
        var gen = new InvoicePdfGenerator();
        var bytes = gen.Generate(SampleInvoice());

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 1000, $"PDF should be at least 1KB, was {bytes.Length}");
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic),
            "Document does not start with the %PDF- magic bytes.");
    }

    [Fact]
    public void Generate_renders_when_irn_is_missing()
    {
        var gen = new InvoicePdfGenerator();
        var model = SampleInvoice(irn: null, isSandbox: false);
        var bytes = gen.Generate(model);

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }

    [Fact]
    public void Generate_with_sandbox_irn_emits_a_larger_document_than_without()
    {
        // Smoke check: the sandbox watermark adds layout boxes, which
        // should make the rendered PDF strictly larger. This is a cheap
        // proxy for "the watermark code path executed", without taking a
        // PdfPig dep just for one assertion.
        var gen = new InvoicePdfGenerator();
        var withWatermark = gen.Generate(SampleInvoice(irn: "SANDBOX-IRN-XYZ", isSandbox: true));
        var withoutWatermark = gen.Generate(SampleInvoice(irn: "IRN-LIVE-1234", isSandbox: false));

        Assert.True(withWatermark.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
        Assert.True(withoutWatermark.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
        Assert.True(withWatermark.Length > withoutWatermark.Length,
            $"Sandbox PDF ({withWatermark.Length} bytes) should be larger than non-sandbox PDF ({withoutWatermark.Length} bytes).");
    }

    [Fact]
    public void Generate_is_deterministic_for_repeated_calls_size_within_tolerance()
    {
        // Repeated rendering of the same model produces PDFs of the same
        // approximate size — guards against accidental inclusion of
        // wall-clock timestamps in document content (vs metadata).
        var gen = new InvoicePdfGenerator();
        var first = gen.Generate(SampleInvoice());
        var second = gen.Generate(SampleInvoice());

        // Allow up to 5% byte-length variation for PDF metadata timestamps.
        var delta = Math.Abs(first.Length - second.Length);
        Assert.True(delta < first.Length * 0.05,
            $"Repeated invoice renders varied by {delta} bytes (>{first.Length * 0.05:N0}); content may include non-deterministic data.");
    }
}
