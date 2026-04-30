using System.Text;
using Xunit;

namespace NickFinance.Pdf.Tests;

[Collection("pdf")]
public sealed class WhtCertificatePdfGeneratorTests
{
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");

    private static WhtCertificatePdfModel SampleCert(string vendor = "Star Oil Tema") => new(
        VendorName: vendor,
        VendorTin: "P0099887766",
        Year: 2026,
        Payments: new List<WhtCertificatePaymentLine>
        {
            new(new DateOnly(2026, 2, 14), "GCB-TXN-1001", "AP-2026-02-00007", 100_000_00, 7.5m, 7_500_00),
            new(new DateOnly(2026, 5, 22), "GCB-TXN-1042", "AP-2026-05-00031", 50_000_00, 7.5m, 3_750_00),
        },
        TotalGrossMinor: 150_000_00,
        TotalWhtMinor: 11_250_00,
        CurrencyCode: "GHS");

    [Fact]
    public void Generate_renders_a_single_vendor_certificate()
    {
        var gen = new WhtCertificatePdfGenerator();
        var bytes = gen.Generate(new[] { SampleCert() });

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }

    [Fact]
    public void Generate_renders_multi_vendor_book_with_page_breaks()
    {
        // Two vendors → two pages → strictly larger than the single-vendor
        // version. Each Page() call in the generator forces a hard break.
        var gen = new WhtCertificatePdfGenerator();
        var single = gen.Generate(new[] { SampleCert() });
        var book = gen.Generate(new[]
        {
            SampleCert("Star Oil Tema"),
            SampleCert("Adom Plumbing"),
            SampleCert("Akorabo Logistics"),
        });

        Assert.True(book.Length > single.Length,
            $"3-vendor book ({book.Length} bytes) should be larger than 1-vendor cert ({single.Length}).");
    }

    [Fact]
    public void Generate_throws_on_empty_input()
    {
        var gen = new WhtCertificatePdfGenerator();
        Assert.Throws<ArgumentException>(() => gen.Generate(Array.Empty<WhtCertificatePdfModel>()));
    }
}
