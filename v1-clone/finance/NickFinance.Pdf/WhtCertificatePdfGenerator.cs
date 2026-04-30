using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NickFinance.Pdf;

/// <summary>
/// QuestPDF implementation of <see cref="IWhtCertificatePdfGenerator"/>.
/// Each input certificate gets its own page (or pages — long lists wrap
/// naturally) with a guaranteed page break between vendors. Layout
/// mirrors the GRA template: certificate title block, payer (Nick
/// TC-Scan) + payee blocks side by side, payment table, totals, signature.
/// </summary>
public sealed class WhtCertificatePdfGenerator : IWhtCertificatePdfGenerator
{
    /// <inheritdoc/>
    public byte[] Generate(IReadOnlyList<WhtCertificatePdfModel> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        if (certificates.Count == 0)
        {
            throw new ArgumentException("At least one certificate is required.", nameof(certificates));
        }

        var doc = Document.Create(container =>
        {
            for (var i = 0; i < certificates.Count; i++)
            {
                var cert = certificates[i];
                var isLast = i == certificates.Count - 1;
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(PdfBranding.MarginPoints);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(t => t.FontSize(PdfBranding.BodyFontSize).FontColor(PdfBranding.ColorSlateBody));

                    page.Header().Element(h => ComposeHeader(h, cert));
                    page.Content().Element(c => ComposeContent(c, cert));
                    page.Footer().Element(f => ComposeFooter(f, cert));
                });
                _ = isLast; // QuestPDF gives each Page() its own break implicitly.
            }
        });

        return doc.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, WhtCertificatePdfModel cert)
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
                col.Item().AlignRight().Text("WHT CERTIFICATE")
                    .FontSize(PdfBranding.HeadingFontSize).Bold()
                    .FontColor(PdfBranding.ColorSlateBody);
                col.Item().AlignRight().Text($"Year of assessment: {cert.Year}")
                    .FontSize(PdfBranding.SubheadingFontSize)
                    .FontColor(PdfBranding.ColorSlateMuted);
                col.Item().AlignRight().Text("Issued under the Income Tax Act, 2015 (Act 896) — Sixth Schedule")
                    .FontSize(PdfBranding.SmallFontSize)
                    .FontColor(PdfBranding.ColorSlateMuted);
            });
        });
    }

    private static void ComposeContent(IContainer container, WhtCertificatePdfModel cert)
    {
        container.Column(col =>
        {
            col.Spacing(12);

            // Payer + payee block.
            col.Item().Row(row =>
            {
                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("PAYER (WITHHOLDING AGENT)").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(PdfBranding.CompanyName).Bold();
                    c.Item().Text($"TIN: {PdfBranding.CompanyTin}").FontSize(PdfBranding.SmallFontSize);
                    c.Item().Text(PdfBranding.CompanyAddressLine1).FontSize(PdfBranding.SmallFontSize);
                    c.Item().Text(PdfBranding.CompanyAddressLine2).FontSize(PdfBranding.SmallFontSize);
                });

                row.ConstantItem(12);

                row.RelativeItem().Background(PdfBranding.ColorSurface).Padding(10).Column(c =>
                {
                    c.Item().Text("PAYEE (VENDOR)").FontSize(PdfBranding.SmallFontSize).Bold()
                        .FontColor(PdfBranding.ColorSlateMuted);
                    c.Item().PaddingTop(2).Text(cert.VendorName).Bold();
                    if (!string.IsNullOrWhiteSpace(cert.VendorTin))
                    {
                        c.Item().Text($"TIN: {cert.VendorTin}").FontSize(PdfBranding.SmallFontSize);
                    }
                    else
                    {
                        c.Item().Text("TIN: (not on file)").FontSize(PdfBranding.SmallFontSize)
                            .FontColor(PdfBranding.ColorSlateMuted).Italic();
                    }
                });
            });

            // Payments table.
            col.Item().Element(c => ComposePaymentsTable(c, cert));

            // Totals strip.
            col.Item().AlignRight().Column(totals =>
            {
                totals.Item().Width(280).Padding(2).Row(r =>
                {
                    r.RelativeItem().Text("Total gross paid").FontColor(PdfBranding.ColorSlateMuted);
                    r.ConstantItem(120).AlignRight().Text(MoneyFormatter.Format(cert.TotalGrossMinor, cert.CurrencyCode));
                });
                totals.Item().Width(280).Background(PdfBranding.ColorEmerald).Padding(8).Row(r =>
                {
                    r.RelativeItem().Text("TOTAL WHT WITHHELD").FontColor(Colors.White).Bold();
                    r.ConstantItem(120).AlignRight().Text(MoneyFormatter.Format(cert.TotalWhtMinor, cert.CurrencyCode))
                        .FontColor(Colors.White).Bold();
                });
            });

            // Statutory note.
            col.Item().PaddingTop(8).Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted));
                t.Span("This certificate confirms the amount of withholding tax deducted from payments to the named vendor for the year of assessment shown. The vendor may claim this against their final tax position with the Ghana Revenue Authority.");
            });

            // Signature block.
            col.Item().PaddingTop(20).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().BorderBottom(0.75f).BorderColor(PdfBranding.ColorSlateMuted).Height(28);
                    c.Item().PaddingTop(2).Text("Authorised signature & date")
                        .FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted);
                });
                row.ConstantItem(20);
                row.RelativeItem().Column(c =>
                {
                    c.Item().BorderBottom(0.75f).BorderColor(PdfBranding.ColorSlateMuted).Height(28);
                    c.Item().PaddingTop(2).Text("Company stamp")
                        .FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted);
                });
            });
        });
    }

    private static void ComposePaymentsTable(IContainer container, WhtCertificatePdfModel cert)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(80);   // date
                c.RelativeColumn(2);    // payment ref
                c.RelativeColumn(2);    // invoice no
                c.RelativeColumn(2);    // gross
                c.ConstantColumn(50);   // rate %
                c.RelativeColumn(2);    // wht
            });

            table.Header(h =>
            {
                h.Cell().Element(HeaderCell).Text("Date");
                h.Cell().Element(HeaderCell).Text("Payment ref");
                h.Cell().Element(HeaderCell).Text("Invoice no");
                h.Cell().Element(HeaderCell).AlignRight().Text("Gross paid");
                h.Cell().Element(HeaderCell).AlignRight().Text("Rate");
                h.Cell().Element(HeaderCell).AlignRight().Text("WHT withheld");
            });

            foreach (var p in cert.Payments)
            {
                table.Cell().Element(BodyCell).Text(p.PaymentDate.ToString("yyyy-MM-dd"));
                table.Cell().Element(BodyCell).Text(p.PaymentRef).FontFamily(Fonts.CourierNew);
                table.Cell().Element(BodyCell).Text(p.InvoiceNo ?? "—").FontFamily(Fonts.CourierNew);
                table.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(p.GrossMinor, cert.CurrencyCode));
                table.Cell().Element(BodyCell).AlignRight().Text($"{p.WhtRatePct.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}%");
                table.Cell().Element(BodyCell).AlignRight().Text(MoneyFormatter.Format(p.WhtMinor, cert.CurrencyCode));
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

    private static void ComposeFooter(IContainer container, WhtCertificatePdfModel cert)
    {
        container.PaddingTop(6).BorderTop(0.5f).BorderColor(PdfBranding.ColorBorder).Column(col =>
        {
            col.Item().PaddingTop(4).AlignCenter().Text(PdfBranding.CompanyFooterLine)
                .FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted);
            col.Item().AlignCenter().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(PdfBranding.SmallFontSize).FontColor(PdfBranding.ColorSlateMuted));
                t.Span($"Vendor: {cert.VendorName} · Year {cert.Year} · Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        });
    }
}
