using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NickFinance.Pdf;

/// <summary>
/// QuestPDF implementation of <see cref="ICustomerStatementPdfGenerator"/>.
/// Mirrors the visual language of the invoice / voucher / receipt
/// generators: A4 portrait, Nick TC-Scan brand block, surface-coloured
/// info cards, indigo table headers, emerald totals strip, hairline
/// borders. Ageing summary lives at the foot when supplied.
/// </summary>
public sealed class CustomerStatementPdfGenerator : ICustomerStatementPdfGenerator
{
    /// <inheritdoc/>
    public byte[] Generate(CustomerStatementPdfModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(PdfBranding.MarginPoints);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontSize(PdfBranding.BodyFontSize).FontColor(PdfBranding.ColorSlateBody));

                page.Header().Element(h => ComposeHeader(h, model));
                page.Content().Element(c => ComposeContent(c, model));
                page.Footer().Element(ComposeFooter);
            });
        });

        return doc.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, CustomerStatementPdfModel model)
    {
        container.PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Row(r =>
                {
                    r.ConstantItem(36).Height(36).Background(PdfBranding.ColorIndigo);
                    r.RelativeItem().PaddingLeft(8).AlignMiddle().Column(inner =>
                    {
                        inner.Item().Text(PdfBranding.CompanyName)
                            .FontSize(PdfBranding.HeadingFontSize).Bold()
                            .FontColor(PdfBranding.ColorIndigo);
                        inner.Item().Text(PdfBranding.CompanyTagline)
                            .FontSize(PdfBranding.SmallFontSize)
                            .FontColor(PdfBranding.ColorSlateMuted);
                    });
                });
            });

            row.RelativeItem().AlignRight().Column(col =>
            {
                col.Item().AlignRight().Text("CUSTOMER STATEMENT")
                    .FontSize(PdfBranding.HeadingFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateBody);
                col.Item().AlignRight().Text($"{model.PeriodFrom:yyyy-MM-dd} – {model.PeriodTo:yyyy-MM-dd}")
                    .FontSize(PdfBranding.SubheadingFontSize)
                    .FontColor(PdfBranding.ColorSlateMuted);
                col.Item().AlignRight().Text($"Currency: {model.CurrencyCode}")
                    .FontSize(PdfBranding.SmallFontSize);
            });
        });
    }

    private static void ComposeContent(IContainer container, CustomerStatementPdfModel model)
    {
        container.Column(col =>
        {
            col.Spacing(12);

            // Customer block + period meta.
            col.Item().Row(row =>
            {
                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("STATEMENT FOR").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(model.CustomerName).Bold();
                    if (!string.IsNullOrWhiteSpace(model.CustomerAddress))
                    {
                        c.Item().Text(model.CustomerAddress!).FontSize(PdfBranding.SmallFontSize);
                    }
                    if (!string.IsNullOrWhiteSpace(model.CustomerTin))
                    {
                        c.Item().PaddingTop(2).Text($"TIN: {model.CustomerTin}").FontSize(PdfBranding.SmallFontSize);
                    }
                });

                row.ConstantItem(12);

                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("PERIOD").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text($"From: {model.PeriodFrom:yyyy-MM-dd}").FontSize(PdfBranding.SmallFontSize);
                    c.Item().Text($"To:   {model.PeriodTo:yyyy-MM-dd}").FontSize(PdfBranding.SmallFontSize);
                    c.Item().PaddingTop(4).Text("Closing balance").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().Text(MoneyFormatter.Format(model.ClosingBalanceMinor, model.CurrencyCode)).Bold();
                });
            });

            // Ledger rows table.
            col.Item().Element(c => ComposeLinesTable(c, model));

            // Closing strip.
            col.Item().AlignRight().Column(strip =>
            {
                strip.Item().Width(260).Background(PdfBranding.ColorEmerald).Padding(8).Row(r =>
                {
                    r.RelativeItem().Text("CLOSING BALANCE").FontColor(Colors.White).Bold();
                    r.ConstantItem(110).AlignRight().Text(MoneyFormatter.Format(model.ClosingBalanceMinor, model.CurrencyCode))
                        .FontColor(Colors.White).Bold();
                });
            });

            // Ageing — when supplied.
            if (model.Ageing is { } age)
            {
                col.Item().Element(c => ComposeAgeing(c, age, model.CurrencyCode));
            }
        });
    }

    private static void ComposeLinesTable(IContainer container, CustomerStatementPdfModel model)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(80);   // date
                c.RelativeColumn(2);    // type
                c.RelativeColumn(3);    // ref
                c.RelativeColumn(2);    // debit
                c.RelativeColumn(2);    // credit
                c.RelativeColumn(2);    // running
            });

            table.Header(h =>
            {
                h.Cell().Element(HeaderCell).Text("Date");
                h.Cell().Element(HeaderCell).Text("Type");
                h.Cell().Element(HeaderCell).Text("Reference");
                h.Cell().Element(HeaderCell).AlignRight().Text("Debit");
                h.Cell().Element(HeaderCell).AlignRight().Text("Credit");
                h.Cell().Element(HeaderCell).AlignRight().Text("Balance");
            });

            // Opening balance row — always emitted, even at zero, so the
            // reader can see the starting position even if no movements.
            table.Cell().Element(BodyCell).Text(model.PeriodFrom.AddDays(-1).ToString("yyyy-MM-dd"));
            table.Cell().Element(BodyCell).Text("Opening balance").Italic().FontColor(PdfBranding.ColorSlateMuted);
            table.Cell().Element(BodyCell).Text(string.Empty);
            table.Cell().Element(BodyCell).AlignRight().Text(string.Empty);
            table.Cell().Element(BodyCell).AlignRight().Text(string.Empty);
            table.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(model.OpeningBalanceMinor, model.CurrencyCode)).Italic();

            foreach (var line in model.Lines)
            {
                table.Cell().Element(BodyCell).Text(line.Date.ToString("yyyy-MM-dd"));
                table.Cell().Element(BodyCell).Text(line.DocumentType);
                table.Cell().Element(BodyCell).Text(line.DocumentRef).FontFamily(Fonts.CourierNew);
                table.Cell().Element(BodyCell).AlignRight().Text(line.DebitMinor == 0
                    ? string.Empty
                    : MoneyFormatter.Format(line.DebitMinor, model.CurrencyCode));
                table.Cell().Element(BodyCell).AlignRight().Text(line.CreditMinor == 0
                    ? string.Empty
                    : MoneyFormatter.Format(line.CreditMinor, model.CurrencyCode));
                table.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(line.RunningBalanceMinor, model.CurrencyCode));
            }
        });

        static IContainer HeaderCell(IContainer c) => c
            .Background(PdfBranding.ColorIndigo)
            .Padding(6)
            .DefaultTextStyle(t => t.FontColor(Colors.White).Bold().FontSize(PdfBranding.SmallFontSize));

        static IContainer BodyCell(IContainer c) => c
            .BorderBottom(0.5f).BorderColor(PdfBranding.ColorBorder)
            .Padding(6)
            .DefaultTextStyle(t => t.FontSize(PdfBranding.BodyFontSize));
    }

    private static void ComposeAgeing(IContainer container, AgeingSummary age, string currency)
    {
        container.PaddingTop(4).Column(col =>
        {
            col.Item().Text("AGEING SUMMARY")
                .FontSize(PdfBranding.SmallFontSize).Bold()
                .FontColor(PdfBranding.ColorSlateMuted);
            col.Item().PaddingTop(4).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                    c.RelativeColumn();
                });
                t.Header(h =>
                {
                    h.Cell().Element(HeaderCell).AlignRight().Text("Current");
                    h.Cell().Element(HeaderCell).AlignRight().Text("31–60");
                    h.Cell().Element(HeaderCell).AlignRight().Text("61–90");
                    h.Cell().Element(HeaderCell).AlignRight().Text("91–120");
                    h.Cell().Element(HeaderCell).AlignRight().Text("120+");
                });
                t.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(age.CurrentMinor, currency));
                t.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(age.Days30Minor, currency));
                t.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(age.Days60Minor, currency));
                t.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(age.Days90Minor, currency));
                t.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(age.Days120PlusMinor, currency));
            });
        });

        static IContainer HeaderCell(IContainer c) => c
            .Background(PdfBranding.ColorIndigo)
            .Padding(6)
            .DefaultTextStyle(t => t.FontColor(Colors.White).Bold().FontSize(PdfBranding.SmallFontSize));

        static IContainer BodyCell(IContainer c) => c
            .BorderBottom(0.5f).BorderColor(PdfBranding.ColorBorder)
            .Padding(6)
            .DefaultTextStyle(t => t.FontSize(PdfBranding.BodyFontSize));
    }

    private static void ComposeFooter(IContainer container)
    {
        container.PaddingTop(6).BorderTop(0.5f).BorderColor(PdfBranding.ColorBorder).Column(col =>
        {
            col.Item().PaddingTop(4).AlignCenter().Text(PdfBranding.CompanyFooterLine)
                .FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted);
            col.Item().AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        });
    }
}
