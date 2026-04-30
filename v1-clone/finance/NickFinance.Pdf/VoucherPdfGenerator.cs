using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NickFinance.Pdf;

/// <summary>
/// QuestPDF implementation of <see cref="IVoucherPdfGenerator"/>. Produces
/// the same A4-portrait NickFinance look as the invoice PDF, but lays out
/// requester / approver / disbursement attribution prominently — the
/// finance team's audit pack expects to see "who did what when" on the
/// face of the voucher.
/// </summary>
public sealed class VoucherPdfGenerator : IVoucherPdfGenerator
{
    /// <inheritdoc/>
    public byte[] Generate(VoucherPdfModel model)
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

    private static void ComposeHeader(IContainer container, VoucherPdfModel model)
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
                col.Item().AlignRight().Text("PETTY CASH VOUCHER")
                    .FontSize(PdfBranding.HeadingFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateBody);
                col.Item().AlignRight().Text(model.VoucherNo)
                    .FontSize(PdfBranding.SubheadingFontSize)
                    .FontColor(PdfBranding.ColorSlateMuted)
                    .FontFamily(Fonts.CourierNew);
                col.Item().AlignRight().Text($"Status: {model.Status}")
                    .FontSize(PdfBranding.SmallFontSize);
            });
        });
    }

    private static void ComposeContent(IContainer container, VoucherPdfModel model)
    {
        container.Column(col =>
        {
            col.Spacing(12);

            // Top facts row — requester / approver / disbursed-by
            col.Item().Row(row =>
            {
                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("REQUESTED BY").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(model.RequesterName).Bold();
                    c.Item().Text($"Submitted: {FormatTs(model.SubmittedAt)}").FontSize(PdfBranding.SmallFontSize);
                });

                row.ConstantItem(8);

                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("APPROVED BY").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(model.ApproverName ?? "(pending)").Bold();
                    c.Item().Text($"Decided: {FormatTs(model.DecidedAt)}").FontSize(PdfBranding.SmallFontSize);
                });

                row.ConstantItem(8);

                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("DISBURSED BY").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(model.DisbursedByName ?? "(pending)").Bold();
                    c.Item().Text($"On: {FormatTs(model.DisbursedAt)}").FontSize(PdfBranding.SmallFontSize);
                });
            });

            // Purpose + classification
            col.Item().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
            {
                c.Item().Text("PURPOSE").FontSize(PdfBranding.SmallFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateMuted);
                c.Item().PaddingTop(2).Text(model.Purpose);
                c.Item().PaddingTop(6).Row(r =>
                {
                    r.RelativeItem().Text($"Category: {model.Category}").FontSize(PdfBranding.SmallFontSize);
                    r.RelativeItem().Text($"Tax: {model.TaxTreatment}").FontSize(PdfBranding.SmallFontSize);
                    r.RelativeItem().Text($"WHT: {model.WhtTreatment}").FontSize(PdfBranding.SmallFontSize);
                    if (!string.IsNullOrWhiteSpace(model.ProjectCode))
                    {
                        r.RelativeItem().Text($"Project: {model.ProjectCode}").FontSize(PdfBranding.SmallFontSize);
                    }
                });
                if (!string.IsNullOrWhiteSpace(model.PayeeName))
                {
                    c.Item().PaddingTop(2).Text($"Payee: {model.PayeeName}").FontSize(PdfBranding.SmallFontSize);
                }
            });

            // Lines table
            col.Item().Element(c => ComposeLinesTable(c, model));

            // Totals
            col.Item().AlignRight().Column(totals =>
            {
                totals.Item().Width(260).Padding(2).Row(r =>
                {
                    r.RelativeItem().Text("Requested").FontColor(PdfBranding.ColorSlateMuted);
                    r.ConstantItem(110).AlignRight().Text(MoneyFormatter.Format(model.AmountRequestedMinor, model.CurrencyCode));
                });
                if (model.AmountApprovedMinor is { } approved)
                {
                    totals.Item().Width(260).Background(PdfBranding.ColorEmerald).Padding(8).Row(r =>
                    {
                        r.RelativeItem().Text("APPROVED").FontColor(Colors.White).Bold();
                        r.ConstantItem(110).AlignRight().Text(MoneyFormatter.Format(approved, model.CurrencyCode))
                            .FontColor(Colors.White).Bold();
                    });
                }
            });

            // Disbursement channel + reference, if known
            if (!string.IsNullOrWhiteSpace(model.DisbursementChannel) ||
                !string.IsNullOrWhiteSpace(model.DisbursementReference))
            {
                col.Item().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("DISBURSEMENT").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    if (!string.IsNullOrWhiteSpace(model.DisbursementChannel))
                    {
                        c.Item().PaddingTop(2).Text($"Channel: {model.DisbursementChannel}");
                    }
                    if (!string.IsNullOrWhiteSpace(model.DisbursementReference))
                    {
                        c.Item().Text($"Reference: {model.DisbursementReference}").FontFamily(Fonts.CourierNew);
                    }
                });
            }

            // Signature block — printed on every voucher so a wet ink
            // signature can land on a printed copy.
            col.Item().PaddingTop(20).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().BorderBottom(0.75f).BorderColor(PdfBranding.ColorSlateMuted).Height(28);
                    c.Item().PaddingTop(2).Text("Payee signature & date")
                        .FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted);
                });
                row.ConstantItem(20);
                row.RelativeItem().Column(c =>
                {
                    c.Item().BorderBottom(0.75f).BorderColor(PdfBranding.ColorSlateMuted).Height(28);
                    c.Item().PaddingTop(2).Text("Custodian signature & date")
                        .FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted);
                });
            });
        });
    }

    private static void ComposeLinesTable(IContainer container, VoucherPdfModel model)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(28);  // #
                c.RelativeColumn(5);   // description
                c.RelativeColumn(2);   // GL
                c.RelativeColumn(2);   // amount
            });

            table.Header(h =>
            {
                h.Cell().Element(HeaderCell).Text("#");
                h.Cell().Element(HeaderCell).Text("Description");
                h.Cell().Element(HeaderCell).Text("GL Account");
                h.Cell().Element(HeaderCell).AlignRight().Text("Amount");
            });

            foreach (var l in model.Lines)
            {
                table.Cell().Element(BodyCell).Text(l.LineNo.ToString(System.Globalization.CultureInfo.InvariantCulture));
                table.Cell().Element(BodyCell).Text(l.Description);
                table.Cell().Element(BodyCell).Text(l.GlAccount).FontFamily(Fonts.CourierNew);
                table.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(l.GrossAmountMinor, model.CurrencyCode));
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

    private static string FormatTs(DateTimeOffset? ts) =>
        ts is { } v ? v.ToString("yyyy-MM-dd HH:mm 'UTC'") : "—";
}
