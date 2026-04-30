using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NickFinance.Pdf;

/// <summary>
/// QuestPDF implementation of <see cref="IInvoicePdfGenerator"/>. Produces
/// an A4-portrait, 1.5 cm-margin invoice that mirrors the on-screen detail
/// page on <c>/ar/{id}</c>. Pure / deterministic — given the same model
/// the same bytes come out (modulo PDF metadata timestamps that QuestPDF
/// itself stamps).
/// </summary>
public sealed class InvoicePdfGenerator : IInvoicePdfGenerator
{
    /// <inheritdoc/>
    public byte[] Generate(InvoicePdfModel model)
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

    private static void ComposeHeader(IContainer container, InvoicePdfModel model)
    {
        container.PaddingBottom(8).Row(row =>
        {
            // Left: a coloured rectangle as the "logo" + company name.
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

            // Right: invoice number + date block.
            row.RelativeItem().AlignRight().Column(col =>
            {
                col.Item().AlignRight().Text("INVOICE")
                    .FontSize(PdfBranding.HeadingFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateBody);
                col.Item().AlignRight().Text(string.IsNullOrWhiteSpace(model.InvoiceNo) ? "(unissued)" : model.InvoiceNo)
                    .FontSize(PdfBranding.SubheadingFontSize)
                    .FontColor(PdfBranding.ColorSlateMuted);
                col.Item().AlignRight().Text($"Date: {model.InvoiceDate:yyyy-MM-dd}");
                col.Item().AlignRight().Text($"Due: {(model.DueDate is { } d ? d.ToString("yyyy-MM-dd") : "On receipt")}");
            });
        });
    }

    private static void ComposeContent(IContainer container, InvoicePdfModel model)
    {
        container.Column(col =>
        {
            col.Spacing(12);

            // Sandbox watermark strip — only when this came from the stub.
            if (model.IrnIsSandbox)
            {
                col.Item().Background(PdfBranding.ColorAmber).Padding(8)
                    .AlignCenter().Text("SANDBOX — NOT SUBMITTED TO GRA")
                    .FontSize(PdfBranding.SubheadingFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateBody);
            }

            // Bill-to + invoice meta row.
            col.Item().Row(row =>
            {
                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("BILL TO").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(model.CustomerName).Bold();
                    if (!string.IsNullOrWhiteSpace(model.CustomerAddress))
                    {
                        c.Item().Text(model.CustomerAddress).FontSize(PdfBranding.SmallFontSize);
                    }
                    if (!string.IsNullOrWhiteSpace(model.CustomerTin))
                    {
                        c.Item().PaddingTop(2).Text($"TIN: {model.CustomerTin}").FontSize(PdfBranding.SmallFontSize);
                    }
                });

                row.ConstantItem(12); // gutter

                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("INVOICE DETAILS").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    if (!string.IsNullOrWhiteSpace(model.Reference))
                    {
                        c.Item().PaddingTop(2).Text($"Reference: {model.Reference}").FontSize(PdfBranding.SmallFontSize);
                    }
                    c.Item().Text($"Currency: {model.CurrencyCode}").FontSize(PdfBranding.SmallFontSize);
                    if (!string.IsNullOrEmpty(model.EvatIrn))
                    {
                        c.Item().PaddingTop(2).Text("e-VAT IRN").FontSize(PdfBranding.SmallFontSize).Bold()
                            .FontColor(PdfBranding.ColorSlateMuted);
                        c.Item().Text(model.EvatIrn).FontSize(PdfBranding.SmallFontSize)
                            .FontFamily(Fonts.CourierNew);
                    }
                });
            });

            // Line items table.
            col.Item().Element(c => ComposeLinesTable(c, model));

            // Totals strip.
            col.Item().Element(c => ComposeTotals(c, model));

            // IRN + QR placeholder.
            col.Item().Element(c => ComposeIrnQr(c, model));

            // Notes / payment terms.
            if (!string.IsNullOrWhiteSpace(model.Notes))
            {
                col.Item().PaddingTop(4).Text(model.Notes!).FontSize(PdfBranding.SmallFontSize)
                    .FontColor(PdfBranding.ColorSlateMuted);
            }

            col.Item().Text("Payment terms: due as stated above. Pay by bank transfer or MoMo to the accounts on the company website.")
                .FontSize(PdfBranding.SmallFontSize).Italic()
                .FontColor(PdfBranding.ColorSlateMuted);
        });
    }

    private static void ComposeLinesTable(IContainer container, InvoicePdfModel model)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(28);   // #
                c.RelativeColumn(5);    // description
                c.ConstantColumn(50);   // qty
                c.RelativeColumn(2);    // unit price
                c.RelativeColumn(2);    // line total
            });

            table.Header(h =>
            {
                h.Cell().Element(HeaderCell).Text("#");
                h.Cell().Element(HeaderCell).Text("Description");
                h.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                h.Cell().Element(HeaderCell).AlignRight().Text("Unit");
                h.Cell().Element(HeaderCell).AlignRight().Text("Line total");
            });

            foreach (var l in model.Lines)
            {
                table.Cell().Element(BodyCell).Text(l.LineNo.ToString(System.Globalization.CultureInfo.InvariantCulture));
                table.Cell().Element(BodyCell).Text(l.Description);
                table.Cell().Element(BodyCell).AlignRight().Text(l.Quantity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
                table.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(l.UnitPriceMinor, model.CurrencyCode));
                table.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(l.LineTotalMinor, model.CurrencyCode));
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

    private static void ComposeTotals(IContainer container, InvoicePdfModel model)
    {
        container.AlignRight().Column(col =>
        {
            col.Item().Width(260).Padding(2).Row(r =>
            {
                r.RelativeItem().Text("Subtotal (net)").FontColor(PdfBranding.ColorSlateMuted);
                r.ConstantItem(110).AlignRight().Text(MoneyFormatter.Format(model.SubtotalNetMinor, model.CurrencyCode));
            });
            col.Item().Width(260).Padding(2).Row(r =>
            {
                r.RelativeItem().Text("Levies (NHIL+GETFund+COVID)").FontColor(PdfBranding.ColorSlateMuted);
                r.ConstantItem(110).AlignRight().Text(MoneyFormatter.Format(model.LeviesMinor, model.CurrencyCode));
            });
            col.Item().Width(260).Padding(2).Row(r =>
            {
                r.RelativeItem().Text("VAT").FontColor(PdfBranding.ColorSlateMuted);
                r.ConstantItem(110).AlignRight().Text(MoneyFormatter.Format(model.VatMinor, model.CurrencyCode));
            });
            col.Item().Width(260).Background(PdfBranding.ColorEmerald).Padding(8).Row(r =>
            {
                r.RelativeItem().Text("TOTAL").FontColor(Colors.White).Bold();
                r.ConstantItem(110).AlignRight().Text(MoneyFormatter.Format(model.GrossMinor, model.CurrencyCode))
                    .FontColor(Colors.White).Bold();
            });
        });
    }

    private static void ComposeIrnQr(IContainer container, InvoicePdfModel model)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("GRA e-VAT IRN").FontSize(PdfBranding.SmallFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateMuted);
                if (string.IsNullOrEmpty(model.EvatIrn))
                {
                    col.Item().Text("(not yet issued)").FontSize(PdfBranding.SmallFontSize).Italic()
                        .FontColor(PdfBranding.ColorSlateMuted);
                }
                else
                {
                    col.Item().Text(model.EvatIrn).FontFamily(Fonts.CourierNew);
                }
            });

            row.ConstantItem(110).Column(col =>
            {
                if (string.IsNullOrEmpty(model.EvatIrn))
                {
                    col.Item().Border(0.5f).BorderColor(PdfBranding.ColorBorder).Width(96).Height(96)
                        .Background(PdfBranding.ColorSurface).AlignCenter().AlignMiddle()
                        .Text("(QR not yet generated)")
                        .FontSize(7).FontColor(PdfBranding.ColorSlateMuted);
                }
                else
                {
                    // Real QR — encode the IRN. A scanner returning this
                    // value lets the recipient look the invoice up via the
                    // GRA verification portal.
                    var png = QrRenderer.Generate(model.EvatIrn, pixelsPerModule: 6);
                    col.Item().Border(0.5f).BorderColor(PdfBranding.ColorBorder).Width(96).Height(96)
                        .Image(png);
                }
                col.Item().AlignCenter().Text("Verification QR").FontSize(7)
                    .FontColor(PdfBranding.ColorSlateMuted);
            });
        });
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
