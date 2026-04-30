using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NickFinance.Pdf;

/// <summary>
/// QuestPDF implementation of <see cref="IReceiptPdfGenerator"/>. The
/// receipt slip is intentionally short — single page, big amount, the
/// invoice + payment reference, and the standard footer.
/// </summary>
public sealed class ReceiptPdfGenerator : IReceiptPdfGenerator
{
    /// <inheritdoc/>
    public byte[] Generate(ReceiptPdfModel model)
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

    private static void ComposeHeader(IContainer container, ReceiptPdfModel model)
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
                col.Item().AlignRight().Text("RECEIPT")
                    .FontSize(PdfBranding.HeadingFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateBody);
                col.Item().AlignRight().Text(model.ReceiptNo)
                    .FontSize(PdfBranding.SubheadingFontSize)
                    .FontColor(PdfBranding.ColorSlateMuted)
                    .FontFamily(Fonts.CourierNew);
                col.Item().AlignRight().Text($"Date: {model.ReceivedAt:yyyy-MM-dd}");
            });
        });
    }

    private static void ComposeContent(IContainer container, ReceiptPdfModel model)
    {
        container.Column(col =>
        {
            col.Spacing(12);

            // "Received from" block
            col.Item().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
            {
                c.Item().Text("RECEIVED FROM").FontSize(PdfBranding.SmallFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateMuted);
                c.Item().PaddingTop(2).Text(model.CustomerName).Bold();
            });

            // The amount strip — the visual centrepiece of the receipt.
            col.Item().Background(PdfBranding.ColorEmerald).Padding(16).Row(r =>
            {
                r.RelativeItem().AlignMiddle().Text("AMOUNT RECEIVED")
                    .FontColor(Colors.White).Bold().FontSize(PdfBranding.SubheadingFontSize);
                r.RelativeItem().AlignRight().AlignMiddle().Text(MoneyFormatter.Format(model.AmountMinor, model.CurrencyCode))
                    .FontColor(Colors.White).Bold().FontSize(20f);
            });

            // Invoice / reference details
            col.Item().Row(row =>
            {
                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("APPLIED TO INVOICE").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(model.InvoiceNo).FontFamily(Fonts.CourierNew);
                    if (model.InvoiceOutstandingMinor is { } outstanding)
                    {
                        c.Item().PaddingTop(4).Text(
                            outstanding == 0
                                ? "Invoice settled in full."
                                : $"Outstanding after this receipt: {MoneyFormatter.Format(outstanding, model.CurrencyCode)}")
                            .FontSize(PdfBranding.SmallFontSize)
                            .FontColor(PdfBranding.ColorSlateMuted);
                    }
                });

                row.ConstantItem(12);

                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("PAYMENT METHOD").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(model.PaymentMethod);
                    if (!string.IsNullOrWhiteSpace(model.Reference))
                    {
                        c.Item().PaddingTop(4).Text("Reference").FontSize(PdfBranding.SmallFontSize).Bold()
                            .FontColor(PdfBranding.ColorSlateMuted);
                        c.Item().Text(model.Reference!).FontFamily(Fonts.CourierNew)
                            .FontSize(PdfBranding.SmallFontSize);
                    }
                });
            });

            col.Item().PaddingTop(20).AlignCenter().Text("Thank you for your payment.")
                .Italic().FontColor(PdfBranding.ColorSlateMuted)
                .FontSize(PdfBranding.SubheadingFontSize);
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
