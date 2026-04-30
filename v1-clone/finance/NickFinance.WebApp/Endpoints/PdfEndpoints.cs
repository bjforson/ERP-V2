using Microsoft.EntityFrameworkCore;
using NickFinance.AR;
using NickERP.Platform.Identity;
using NickFinance.Pdf;
using NickFinance.PettyCash;
using NickFinance.WebApp.Services;

namespace NickFinance.WebApp.Endpoints;

/// <summary>
/// HTTP endpoints that stream PDF artefacts (invoice, voucher, receipt)
/// for the corresponding entity. They sit alongside the Blazor circuit so
/// a Razor page can link to them with a simple anchor + <c>target="_blank"</c>.
///
/// Tenant scoping piggybacks on the EF query filters wired into the
/// per-module DbContexts — fetching by id alone is enough; a row from a
/// foreign tenant simply doesn't appear and the endpoint returns 404.
/// </summary>
public static class PdfEndpoints
{
    /// <summary>Map every <c>/pdf/...</c> route. Call after
    /// <c>app.MapRazorComponents&lt;App&gt;()</c> in <c>Program.cs</c>.</summary>
    public static void MapPdfEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // --- Invoice ---
        app.MapGet("/pdf/invoice/{id:guid}", async (
                Guid id,
                ArDbContext db,
                IInvoicePdfGenerator gen,
                CurrentUser user,
                CancellationToken ct) =>
            {
                var inv = await db.Invoices
                    .Include(i => i.Lines)
                    .FirstOrDefaultAsync(i => i.ArInvoiceId == id, ct);
                if (inv is null) return Results.NotFound();

                var customer = await db.Customers
                    .FirstOrDefaultAsync(c => c.CustomerId == inv.CustomerId, ct);

                var model = BuildInvoiceModel(inv, customer);
                var bytes = gen.Generate(model);
                var fileName = string.IsNullOrWhiteSpace(inv.InvoiceNo)
                    ? $"invoice-{inv.ArInvoiceId:N}.pdf"
                    : $"invoice-{inv.InvoiceNo}.pdf";
                return Results.File(bytes, "application/pdf", fileName);
            })
            .RequireAuthorization();

        // --- Voucher ---
        app.MapGet("/pdf/voucher/{id:guid}", async (
                Guid id,
                PettyCashDbContext db,
                IdentityDbContext idDb,
                IVoucherPdfGenerator gen,
                CurrentUser user,
                CancellationToken ct) =>
            {
                var v = await db.Vouchers
                    .Include(x => x.Lines)
                    .FirstOrDefaultAsync(x => x.VoucherId == id, ct);
                if (v is null) return Results.NotFound();

                // Resolve display names from the identity schema. We pull
                // the three rows we need in a single round-trip; the
                // resulting dictionary is keyed by InternalUserId so
                // BuildVoucherModel can look each up without a second hop.
                var ids = new List<Guid> { v.RequesterUserId };
                if (v.DecidedByUserId is { } a) ids.Add(a);
                if (v.DisbursedByUserId is { } d) ids.Add(d);
                var nameLookup = await idDb.Users
                    .Where(u => ids.Contains(u.InternalUserId))
                    .ToDictionaryAsync(u => u.InternalUserId, u => u.DisplayName, ct);

                var model = BuildVoucherModel(v, nameLookup);
                var bytes = gen.Generate(model);
                var fileName = string.IsNullOrWhiteSpace(v.VoucherNo)
                    ? $"voucher-{v.VoucherId:N}.pdf"
                    : $"voucher-{v.VoucherNo}.pdf";
                return Results.File(bytes, "application/pdf", fileName);
            })
            .RequireAuthorization();

        // --- Receipt ---
        app.MapGet("/pdf/receipt/{id:guid}", async (
                Guid id,
                ArDbContext db,
                IReceiptPdfGenerator gen,
                CurrentUser user,
                CancellationToken ct) =>
            {
                var receipt = await db.Receipts
                    .FirstOrDefaultAsync(r => r.ArReceiptId == id, ct);
                if (receipt is null) return Results.NotFound();

                var inv = await db.Invoices
                    .FirstOrDefaultAsync(i => i.ArInvoiceId == receipt.ArInvoiceId, ct);
                if (inv is null) return Results.NotFound();

                var customer = await db.Customers
                    .FirstOrDefaultAsync(c => c.CustomerId == inv.CustomerId, ct);

                var model = BuildReceiptModel(receipt, inv, customer);
                var bytes = gen.Generate(model);
                var fileName = $"receipt-{inv.InvoiceNo}-{receipt.ArReceiptId.ToString("N")[..8]}.pdf";
                return Results.File(bytes, "application/pdf", fileName);
            })
            .RequireAuthorization();

        // --- Customer statement ---
        app.MapGet("/pdf/statement/{customerId:guid}", async (
                Guid customerId,
                DateOnly? from,
                DateOnly? to,
                ICustomerStatementService statementSvc,
                ICustomerStatementPdfGenerator gen,
                CurrentUser user,
                CancellationToken ct) =>
            {
                // Default window: last 60 days. Most ops just want
                // "what's outstanding right now" — passing dates is for
                // the audit / month-end use case.
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var f = from ?? today.AddDays(-60);
                var t = to ?? today;
                if (t < f) return Results.BadRequest("'to' must be on or after 'from'.");

                var stmt = await statementSvc.BuildAsync(customerId, f, t, user.TenantId, ct);
                var ageing = await statementSvc.ComputeAgeingAsync(customerId, t, user.TenantId, ct);

                var model = BuildStatementModel(stmt, ageing);
                var bytes = gen.Generate(model);
                var fileName = $"statement-{stmt.Customer.Code}-{f:yyyyMMdd}-{t:yyyyMMdd}.pdf";
                return Results.File(bytes, "application/pdf", fileName);
            })
            .RequireAuthorization();

        // --- WHT single vendor certificate ---
        app.MapGet("/pdf/wht-certificate/{vendorId:guid}/{year:int}", async (
                Guid vendorId,
                int year,
                NickFinance.AP.IWhtCertificateService whtSvc,
                IWhtCertificatePdfGenerator gen,
                CurrentUser user,
                CancellationToken ct) =>
            {
                var data = await whtSvc.GetForYearAsync(year, user.TenantId, ct);
                var match = data.FirstOrDefault(d => d.VendorId == vendorId);
                if (match is null) return Results.NotFound();

                var model = BuildWhtCertificateModel(match);
                var bytes = gen.Generate(new[] { model });
                var fileName = $"wht-cert-{year}-{match.VendorName.Replace(' ', '_')}.pdf";
                return Results.File(bytes, "application/pdf", fileName);
            })
            .RequireAuthorization();

        // --- WHT certificate book (whole year, all vendors) ---
        app.MapGet("/pdf/wht-certificate-book/{year:int}", async (
                int year,
                NickFinance.AP.IWhtCertificateService whtSvc,
                IWhtCertificatePdfGenerator gen,
                CurrentUser user,
                CancellationToken ct) =>
            {
                var data = await whtSvc.GetForYearAsync(year, user.TenantId, ct);
                if (data.Count == 0) return Results.NotFound();

                var models = data.Select(BuildWhtCertificateModel).ToList();
                var bytes = gen.Generate(models);
                return Results.File(bytes, "application/pdf", $"wht-certificate-book-{year}.pdf");
            })
            .RequireAuthorization();

        // --- Email statement to customer ---
        app.MapPost("/api/email/statement/{customerId:guid}", async (
                Guid customerId,
                DateOnly? from,
                DateOnly? to,
                ArDbContext db,
                ICustomerStatementService statementSvc,
                ICustomerStatementPdfGenerator gen,
                IEmailService email,
                CurrentUser user,
                CancellationToken ct) =>
            {
                var customer = await db.Customers.FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);
                if (customer is null) return Results.NotFound();
                if (string.IsNullOrWhiteSpace(customer.Email))
                {
                    return Results.BadRequest(new { error = "Customer has no email address on file." });
                }

                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                var f = from ?? today.AddDays(-60);
                var t = to ?? today;
                var stmt = await statementSvc.BuildAsync(customerId, f, t, user.TenantId, ct);
                var ageing = await statementSvc.ComputeAgeingAsync(customerId, t, user.TenantId, ct);
                var model = BuildStatementModel(stmt, ageing);
                var bytes = gen.Generate(model);

                var subject = $"Statement of account — {f:yyyy-MM-dd} to {t:yyyy-MM-dd}";
                var html = $"<p>Dear {System.Net.WebUtility.HtmlEncode(customer.Name)},</p>"
                         + $"<p>Please find attached your statement of account for the period {f:yyyy-MM-dd} to {t:yyyy-MM-dd}.</p>"
                         + $"<p>Closing balance: <strong>{(stmt.ClosingBalanceMinor / 100m).ToString("0.00")} {stmt.CurrencyCode}</strong>.</p>"
                         + $"<p>Regards,<br/>{PdfBranding.CompanyName}</p>";
                var ok = await email.SendAsync(new EmailMessage(
                    To: customer.Email,
                    Subject: subject,
                    BodyHtml: html,
                    BodyText: $"Statement attached. Closing balance {stmt.ClosingBalanceMinor / 100m:0.00} {stmt.CurrencyCode}.",
                    Attachments: new[]
                    {
                        new EmailAttachment(
                            Filename: $"statement-{customer.Code}-{f:yyyyMMdd}-{t:yyyyMMdd}.pdf",
                            ContentType: "application/pdf",
                            Content: bytes)
                    }), ct);

                return ok
                    ? Results.Ok(new { sent = true, recipient = customer.Email })
                    : Results.Problem("Email send failed or SMTP not configured. See server logs.");
            })
            .RequireAuthorization();
    }

    // ---- Model builders ------------------------------------------------

    internal static InvoicePdfModel BuildInvoiceModel(ArInvoice inv, Customer? customer)
    {
        ArgumentNullException.ThrowIfNull(inv);

        var lines = inv.Lines
            .OrderBy(l => l.LineNo)
            .Select(l => new InvoicePdfLine(
                LineNo: l.LineNo,
                Description: l.Description,
                Quantity: 1m,                         // Quantity isn't tracked on ArInvoiceLine v1.
                UnitPriceMinor: l.NetAmountMinor,
                LineTotalMinor: l.NetAmountMinor))
            .ToList();

        return new InvoicePdfModel(
            InvoiceNo: inv.InvoiceNo ?? string.Empty,
            InvoiceDate: inv.InvoiceDate,
            DueDate: inv.DueDate,
            CustomerName: customer?.Name ?? "(unknown)",
            CustomerTin: customer?.Tin,
            CustomerAddress: customer?.Address,
            CurrencyCode: inv.CurrencyCode,
            SubtotalNetMinor: inv.SubtotalNetMinor,
            LeviesMinor: inv.LeviesMinor,
            VatMinor: inv.VatMinor,
            GrossMinor: inv.GrossMinor,
            EvatIrn: inv.EvatIrn,
            IrnIsSandbox: StubEvatProvider.IsSandbox(inv.EvatIrn),
            Lines: lines,
            Reference: inv.Reference,
            Notes: null);
    }

    internal static VoucherPdfModel BuildVoucherModel(Voucher v, IReadOnlyDictionary<Guid, string>? nameLookup = null)
    {
        ArgumentNullException.ThrowIfNull(v);

        var lines = v.Lines
            .OrderBy(l => l.LineNo)
            .Select(l => new VoucherPdfLine(
                LineNo: l.LineNo,
                Description: l.Description,
                GlAccount: l.GlAccount,
                GrossAmountMinor: l.GrossAmountMinor))
            .ToList();

        // Resolve display names from the identity-side dictionary the
        // endpoint passes in. Falls back to the short-GUID form for any
        // unresolved id (e.g. a deleted user, a service account, or the
        // dev-only seed row that has no identity.users entry yet).
        string ResolveName(Guid id) =>
            nameLookup is not null && nameLookup.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : $"User {id.ToString("N")[..8]}";

        var requesterDisplay = ResolveName(v.RequesterUserId);
        var approverDisplay = v.DecidedByUserId is { } a ? ResolveName(a) : null;
        var disbursedByDisplay = v.DisbursedByUserId is { } d ? ResolveName(d) : null;

        return new VoucherPdfModel(
            VoucherNo: v.VoucherNo,
            Status: v.Status.ToString(),
            RequesterName: requesterDisplay,
            ApproverName: approverDisplay,
            DisbursedByName: disbursedByDisplay,
            Category: v.Category.ToString(),
            Purpose: v.Purpose,
            PayeeName: v.PayeeName,
            CurrencyCode: v.CurrencyCode,
            AmountRequestedMinor: v.AmountRequestedMinor,
            AmountApprovedMinor: v.AmountApprovedMinor,
            TaxTreatment: v.TaxTreatment.ToString(),
            WhtTreatment: v.WhtTreatment.ToString(),
            ProjectCode: v.ProjectCode,
            DisbursementChannel: v.DisbursementChannel,
            DisbursementReference: v.DisbursementReference,
            Lines: lines,
            CreatedAt: v.CreatedAt,
            SubmittedAt: v.SubmittedAt,
            DecidedAt: v.DecidedAt,
            DisbursedAt: v.DisbursedAt);
    }

    internal static ReceiptPdfModel BuildReceiptModel(ArReceipt receipt, ArInvoice inv, Customer? customer)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(inv);

        return new ReceiptPdfModel(
            ReceiptNo: $"R-{receipt.ArReceiptId.ToString("N")[..8].ToUpperInvariant()}",
            InvoiceNo: inv.InvoiceNo ?? "(unissued)",
            CustomerName: customer?.Name ?? "(unknown)",
            CurrencyCode: receipt.CurrencyCode,
            AmountMinor: receipt.AmountMinor,
            ReceivedAt: receipt.ReceiptDate,
            PaymentMethod: DescribeCashAccount(receipt.CashAccount),
            Reference: receipt.Reference,
            InvoiceOutstandingMinor: inv.OutstandingMinor);
    }

    /// <summary>Map the cash GL account code to the human-readable channel
    /// label that ArReceiptPage offers in its dropdown. Falls back to
    /// "Account {code}" for anything bespoke.</summary>
    private static string DescribeCashAccount(string code) => code switch
    {
        "1010" => "Cash on hand",
        "1020" => "MoMo MTN",
        "1030" => "Bank GCB",
        "1031" => "Bank Ecobank",
        "1032" => "Bank Stanbic",
        _ => $"Account {code}"
    };

    internal static CustomerStatementPdfModel BuildStatementModel(
        CustomerStatement stmt,
        (long CurrentMinor, long Days30Minor, long Days60Minor, long Days90Minor, long Days120PlusMinor) ageing)
    {
        ArgumentNullException.ThrowIfNull(stmt);

        var lines = stmt.Rows
            .Select(r => new StatementLine(
                Date: r.Date,
                DocumentType: r.Type,
                DocumentRef: r.Reference,
                DebitMinor: r.DebitMinor,
                CreditMinor: r.CreditMinor,
                RunningBalanceMinor: r.RunningBalanceMinor))
            .ToList();

        return new CustomerStatementPdfModel(
            CustomerName: stmt.Customer.Name,
            CustomerTin: stmt.Customer.Tin,
            CustomerAddress: stmt.Customer.Address,
            PeriodFrom: stmt.From,
            PeriodTo: stmt.To,
            CurrencyCode: stmt.CurrencyCode,
            OpeningBalanceMinor: stmt.OpeningBalanceMinor,
            ClosingBalanceMinor: stmt.ClosingBalanceMinor,
            Lines: lines,
            Ageing: new AgeingSummary(
                CurrentMinor: ageing.CurrentMinor,
                Days30Minor: ageing.Days30Minor,
                Days60Minor: ageing.Days60Minor,
                Days90Minor: ageing.Days90Minor,
                Days120PlusMinor: ageing.Days120PlusMinor));
    }

    internal static WhtCertificatePdfModel BuildWhtCertificateModel(NickFinance.AP.WhtCertificateData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var payments = data.Payments
            .Select(p => new WhtCertificatePaymentLine(
                PaymentDate: p.Date,
                PaymentRef: p.PaymentRef,
                InvoiceNo: p.InvoiceNo,
                GrossMinor: p.GrossMinor,
                WhtRatePct: p.WhtRatePct,
                WhtMinor: p.WhtMinor))
            .ToList();

        return new WhtCertificatePdfModel(
            VendorName: data.VendorName,
            VendorTin: data.VendorTin,
            Year: data.Year,
            Payments: payments,
            TotalGrossMinor: data.TotalGrossMinor,
            TotalWhtMinor: data.TotalWhtMinor,
            CurrencyCode: "GHS");
    }
}
