using NickHR.Services.Payroll.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NickHR.Services.Payroll;

/// <summary>
/// Generates PDF payslips using QuestPDF.
/// </summary>
public static class PayslipPdfGenerator
{
    static PayslipPdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Generate(PayrollCalculationResult payslip, int month, int year, string companyName = "NickHR Company Ltd")
    {
        var monthName = new DateTime(year, month, 1).ToString("MMMM yyyy");

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(header => ComposeHeader(header, companyName, monthName));
                page.Content().Element(content => ComposeContent(content, payslip));
                page.Footer().Element(ComposeFooter);
            });
        });

        return document.GeneratePdf();
    }

    private static void ComposeHeader(IContainer container, string companyName, string monthName)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(companyName).FontSize(16).Bold().FontColor(Colors.Blue.Darken3);
                    col.Item().Text("Payslip").FontSize(12).SemiBold().FontColor(Colors.Grey.Darken2);
                });
                row.ConstantItem(150).AlignRight().Column(col =>
                {
                    col.Item().Text($"Pay Period: {monthName}").SemiBold();
                    col.Item().Text($"Generated: {DateTime.UtcNow:dd MMM yyyy}").FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
            column.Item().PaddingVertical(5).LineHorizontal(1).LineColor(Colors.Blue.Darken3);
        });
    }

    private static void ComposeContent(IContainer container, PayrollCalculationResult p)
    {
        container.PaddingVertical(10).Column(column =>
        {
            // Employee Info
            column.Item().Background(Colors.Grey.Lighten4).Padding(10).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text($"Employee: {p.EmployeeName}").SemiBold();
                    col.Item().Text($"Code: {p.EmployeeCode}");
                    col.Item().Text($"Department: {p.Department}");
                });
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text($"Designation: {p.Designation}");
                    col.Item().Text($"Bank: {p.BankName ?? "N/A"}");
                    col.Item().Text($"Account: {MaskAccountNumber(p.BankAccountNumber)}");
                });
            });

            column.Item().PaddingVertical(10);

            // Earnings & Deductions side by side
            column.Item().Row(row =>
            {
                // Earnings
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("EARNINGS").FontSize(10).SemiBold().FontColor(Colors.Green.Darken3);
                    col.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(1);
                        });

                        table.Cell().Text("Basic Salary").SemiBold();
                        table.Cell().AlignRight().Text($"GH\u20b5 {p.BasicSalary:N2}").SemiBold();

                        foreach (var a in p.Allowances)
                        {
                            table.Cell().Text(a.Name);
                            table.Cell().AlignRight().Text($"GH\u20b5 {a.Amount:N2}");
                        }

                        if (p.OvertimePay > 0)
                        {
                            table.Cell().Text($"Overtime ({p.OvertimeHours:N1} hrs)");
                            table.Cell().AlignRight().Text($"GH\u20b5 {p.OvertimePay:N2}");
                        }

                        // Gross total
                        table.Cell().PaddingTop(5).BorderTop(1).BorderColor(Colors.Grey.Medium)
                            .Text("Gross Pay").Bold();
                        table.Cell().PaddingTop(5).BorderTop(1).BorderColor(Colors.Grey.Medium)
                            .AlignRight().Text($"GH\u20b5 {p.GrossPay:N2}").Bold();
                    });
                });

                row.ConstantItem(20); // Spacer

                // Deductions
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("DEDUCTIONS").FontSize(10).SemiBold().FontColor(Colors.Red.Darken3);
                    col.Item().PaddingTop(5).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn(3);
                            columns.RelativeColumn(1);
                        });

                        table.Cell().Text("SSNIT (5.5%)");
                        table.Cell().AlignRight().Text($"GH\u20b5 {p.SSNITEmployee:N2}");

                        table.Cell().Text("PAYE Tax");
                        table.Cell().AlignRight().Text($"GH\u20b5 {p.PAYE:N2}");

                        foreach (var d in p.Deductions)
                        {
                            table.Cell().Text(d.Name);
                            table.Cell().AlignRight().Text($"GH\u20b5 {d.Amount:N2}");
                        }

                        // Total deductions
                        table.Cell().PaddingTop(5).BorderTop(1).BorderColor(Colors.Grey.Medium)
                            .Text("Total Deductions").Bold();
                        table.Cell().PaddingTop(5).BorderTop(1).BorderColor(Colors.Grey.Medium)
                            .AlignRight().Text($"GH\u20b5 {p.TotalDeductions:N2}").Bold();
                    });
                });
            });

            column.Item().PaddingVertical(10);

            // Net Pay box
            column.Item().Background(Colors.Blue.Darken3).Padding(12).Row(row =>
            {
                row.RelativeItem().Text("NET PAY").FontSize(14).Bold().FontColor(Colors.White);
                row.RelativeItem().AlignRight().Text($"GH\u20b5 {p.NetPay:N2}").FontSize(14).Bold().FontColor(Colors.White);
            });

            column.Item().PaddingVertical(10);

            // Employer contributions (informational)
            column.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(col =>
            {
                col.Item().Text("EMPLOYER CONTRIBUTIONS (Not deducted from employee)").FontSize(8).SemiBold().FontColor(Colors.Grey.Darken2);
                col.Item().PaddingTop(3).Row(row =>
                {
                    row.RelativeItem().Text("SSNIT Employer (13%)");
                    row.ConstantItem(100).AlignRight().Text($"GH\u20b5 {p.SSNITEmployer:N2}");
                });
            });

            // Tax breakdown
            if (p.TaxBreakdown.Any())
            {
                column.Item().PaddingTop(10).Text("PAYE TAX BREAKDOWN").FontSize(8).SemiBold().FontColor(Colors.Grey.Darken2);
                column.Item().PaddingTop(3).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(3);
                        columns.RelativeColumn(1);
                        columns.RelativeColumn(1);
                        columns.RelativeColumn(1);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Text("Bracket").FontSize(8).SemiBold();
                        header.Cell().AlignRight().Text("Amount").FontSize(8).SemiBold();
                        header.Cell().AlignRight().Text("Rate").FontSize(8).SemiBold();
                        header.Cell().AlignRight().Text("Tax").FontSize(8).SemiBold();
                    });

                    foreach (var b in p.TaxBreakdown)
                    {
                        table.Cell().Text(b.Bracket).FontSize(8);
                        table.Cell().AlignRight().Text($"GH\u20b5 {b.TaxableAmount:N2}").FontSize(8);
                        table.Cell().AlignRight().Text($"{b.Rate:N1}%").FontSize(8);
                        table.Cell().AlignRight().Text($"GH\u20b5 {b.Tax:N2}").FontSize(8);
                    }
                });
            }
        });
    }

    private static void ComposeFooter(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Medium);
            col.Item().PaddingTop(5).Row(row =>
            {
                row.RelativeItem().Text("This is a computer-generated payslip and does not require a signature.")
                    .FontSize(7).FontColor(Colors.Grey.Medium);
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.Span("Generated by NickHR ").FontSize(7).FontColor(Colors.Grey.Medium);
                    text.CurrentPageNumber().FontSize(7);
                    text.Span(" / ").FontSize(7);
                    text.TotalPages().FontSize(7);
                });
            });
        });
    }

    private static string MaskAccountNumber(string? accountNumber)
    {
        if (string.IsNullOrEmpty(accountNumber) || accountNumber.Length < 4)
            return accountNumber ?? "N/A";
        return new string('*', accountNumber.Length - 4) + accountNumber[^4..];
    }

    /// <summary>
    /// Generate a batch of payslips as a single PDF (one page per employee).
    /// </summary>
    public static byte[] GenerateBatch(List<PayrollCalculationResult> payslips, int month, int year, string companyName = "NickHR Company Ltd")
    {
        var monthName = new DateTime(year, month, 1).ToString("MMMM yyyy");

        var document = Document.Create(container =>
        {
            foreach (var payslip in payslips)
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(30);
                    page.DefaultTextStyle(x => x.FontSize(9));

                    page.Header().Element(header => ComposeHeader(header, companyName, monthName));
                    page.Content().Element(content => ComposeContent(content, payslip));
                    page.Footer().Element(ComposeFooter);
                });
            }
        });

        return document.GeneratePdf();
    }
}
