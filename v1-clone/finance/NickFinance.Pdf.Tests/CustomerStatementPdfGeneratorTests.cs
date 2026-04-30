using System.Text;
using Xunit;

namespace NickFinance.Pdf.Tests;

[Collection("pdf")]
public sealed class CustomerStatementPdfGeneratorTests
{
    private static readonly byte[] PdfMagic = Encoding.ASCII.GetBytes("%PDF-");

    private static CustomerStatementPdfModel Sample(
        AgeingSummary? ageing = null,
        IReadOnlyList<StatementLine>? lines = null) => new(
            CustomerName: "Agro Imports Ltd",
            CustomerTin: "C0012345678",
            CustomerAddress: "12 Harbour Rd, Tema",
            PeriodFrom: new DateOnly(2026, 3, 1),
            PeriodTo: new DateOnly(2026, 3, 31),
            CurrencyCode: "GHS",
            OpeningBalanceMinor: 50_000_00,
            ClosingBalanceMinor: 70_000_00,
            Lines: lines ?? new List<StatementLine>
            {
                new(new DateOnly(2026, 3, 5), "Invoice", "INV-2026-03-00012", 30_000_00, 0, 80_000_00),
                new(new DateOnly(2026, 3, 18), "Receipt", "GCB-TXN-9981", 0, 10_000_00, 70_000_00),
            },
            Ageing: ageing);

    [Fact]
    public void Generate_returns_non_empty_pdf_bytes()
    {
        var gen = new CustomerStatementPdfGenerator();
        var bytes = gen.Generate(Sample());

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }

    [Fact]
    public void Generate_renders_with_ageing_summary()
    {
        // Ageing block is opt-in on the model — when supplied it adds a
        // table at the foot, which should make the rendered PDF strictly
        // larger than the non-ageing version.
        var gen = new CustomerStatementPdfGenerator();
        var without = gen.Generate(Sample(ageing: null));
        var with = gen.Generate(Sample(ageing: new AgeingSummary(
            CurrentMinor: 30_000_00,
            Days30Minor: 20_000_00,
            Days60Minor: 10_000_00,
            Days90Minor: 5_000_00,
            Days120PlusMinor: 5_000_00)));

        Assert.True(with.Length > without.Length,
            $"Statement with ageing ({with.Length} bytes) should be larger than without ({without.Length}).");
    }

    [Fact]
    public void Generate_renders_multi_line_statement()
    {
        var gen = new CustomerStatementPdfGenerator();
        var lines = Enumerable.Range(1, 12)
            .Select(i => new StatementLine(
                Date: new DateOnly(2026, 3, i),
                DocumentType: i % 3 == 0 ? "Receipt" : "Invoice",
                DocumentRef: $"REF-{i:D4}",
                DebitMinor: i % 3 == 0 ? 0 : 10_000_00,
                CreditMinor: i % 3 == 0 ? 5_000_00 : 0,
                RunningBalanceMinor: 50_000_00 + (i * 1000_00)))
            .ToList();

        var bytes = gen.Generate(Sample(lines: lines));

        Assert.True(bytes.Length > 1000);
        Assert.True(bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic));
    }
}
