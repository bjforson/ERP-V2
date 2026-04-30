using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using NickFinance.TaxEngine;

namespace NickFinance.AP;

/// <summary>
/// AP module surface. Workflow:
/// <list type="number">
///   <item><description><see cref="UpsertVendorAsync"/> — vendor master.</description></item>
///   <item><description><see cref="CaptureBillAsync"/> — record a bill from vendor with optional Ghana-inclusive tax back-solve.</description></item>
///   <item><description><see cref="ApproveBillAsync"/> — post the journal: DR expense lines + DR VAT input, CR <c>2000 Trade payables</c>.</description></item>
///   <item><description><see cref="PayBillAsync"/> — pay all-or-part of an approved bill, deducting WHT, posting (DR 2000 / CR cash + CR 2150 WHT payable), generating a <see cref="WhtCertificate"/>.</description></item>
/// </list>
/// </summary>
public interface IApService
{
    Task<Vendor> UpsertVendorAsync(UpsertVendorRequest req, CancellationToken ct = default);
    Task<ApBill> CaptureBillAsync(CaptureBillRequest req, CancellationToken ct = default);
    Task<ApBill> ApproveBillAsync(Guid billId, Guid approverUserId, DateOnly effectiveDate, Guid periodId, CancellationToken ct = default);
    Task<(ApPayment Payment, WhtCertificate? Certificate)> PayBillAsync(PayBillRequest req, Guid periodId, CancellationToken ct = default);
    Task<ApBill> VoidBillAsync(Guid billId, Guid actorUserId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<AgingBucket>> AgingReportAsync(DateOnly asOf, long tenantId = 1, CancellationToken ct = default);
}

public sealed record UpsertVendorRequest(
    string Code, string Name, string? Tin = null, bool IsVatRegistered = false,
    WhtTransactionType DefaultWht = WhtTransactionType.SupplyOfServices,
    bool WhtExempt = false,
    string? Email = null, string? Phone = null, string? Address = null,
    string? MomoNumber = null, string? MomoNetwork = null,
    string? BankAccountNumber = null, string? BankName = null,
    string? DefaultExpenseAccount = null,
    long TenantId = 1);

public sealed record CaptureBillRequest(
    Guid VendorId,
    string VendorReference,
    DateOnly BillDate,
    DateOnly DueDate,
    BillTaxTreatment TaxTreatment,
    IReadOnlyList<CaptureBillLine> Lines,
    string? PoReference = null,
    string? GrnReference = null,
    string CurrencyCode = "GHS",
    string? Notes = null,
    long TenantId = 1);

public sealed record CaptureBillLine(
    string Description,
    long AmountMinor,                  // gross under GhanaInclusive, net under PreTax
    string? ExpenseAccountOverride = null);

public enum BillTaxTreatment
{
    /// <summary>Line amounts are pre-tax; AP computes levies + VAT on top.</summary>
    PreTax = 0,
    /// <summary>Line amounts are inclusive of NHIL+GETFund+COVID+VAT — AP back-solves the net.</summary>
    GhanaInclusive = 1,
    /// <summary>No tax decomposition — line amounts post at face value (e.g. payroll allocations).</summary>
    None = 2
}

public sealed record PayBillRequest(
    Guid BillId,
    DateOnly PaymentDate,
    long AmountMinor,
    string PaymentRail,
    string CashAccount,
    Guid PaymentRunId,
    Guid RecordedByUserId,
    string? RailReference = null,
    long TenantId = 1);

public sealed record AgingBucket(string Bucket, int BillCount, Money OutstandingTotal);

public sealed class ApException : Exception
{
    public ApException(string message) : base(message) { }
}

public sealed class ApService : IApService
{
    private readonly ApDbContext _db;
    private readonly ILedgerWriter _ledger;
    private readonly TimeProvider _clock;

    public ApService(ApDbContext db, ILedgerWriter ledger, TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _clock = clock ?? TimeProvider.System;
    }

    // -----------------------------------------------------------------
    // Vendor
    // -----------------------------------------------------------------

    public async Task<Vendor> UpsertVendorAsync(UpsertVendorRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Code)) throw new ArgumentException("Code required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Name required.", nameof(req));

        var existing = await _db.Vendors.FirstOrDefaultAsync(v => v.TenantId == req.TenantId && v.Code == req.Code, ct);
        var now = _clock.GetUtcNow();
        if (existing is null)
        {
            var v = new Vendor
            {
                Code = req.Code.Trim(),
                Name = req.Name.Trim(),
                Tin = req.Tin,
                IsVatRegistered = req.IsVatRegistered,
                DefaultWht = req.DefaultWht,
                WhtExempt = req.WhtExempt,
                Email = req.Email, Phone = req.Phone, Address = req.Address,
                MomoNumber = req.MomoNumber, MomoNetwork = req.MomoNetwork,
                BankAccountNumber = req.BankAccountNumber, BankName = req.BankName,
                DefaultExpenseAccount = req.DefaultExpenseAccount,
                IsActive = true,
                CreatedAt = now, UpdatedAt = now,
                TenantId = req.TenantId
            };
            _db.Vendors.Add(v);
            await _db.SaveChangesAsync(ct);
            return v;
        }
        existing.Name = req.Name.Trim();
        existing.Tin = req.Tin;
        existing.IsVatRegistered = req.IsVatRegistered;
        existing.DefaultWht = req.DefaultWht;
        existing.WhtExempt = req.WhtExempt;
        existing.Email = req.Email; existing.Phone = req.Phone; existing.Address = req.Address;
        existing.MomoNumber = req.MomoNumber; existing.MomoNetwork = req.MomoNetwork;
        existing.BankAccountNumber = req.BankAccountNumber; existing.BankName = req.BankName;
        existing.DefaultExpenseAccount = req.DefaultExpenseAccount;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    // -----------------------------------------------------------------
    // Capture
    // -----------------------------------------------------------------

    public async Task<ApBill> CaptureBillAsync(CaptureBillRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Lines is null || req.Lines.Count == 0) throw new ArgumentException("At least one line required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.VendorReference)) throw new ArgumentException("VendorReference required.", nameof(req));
        if (req.DueDate < req.BillDate) throw new ArgumentException("DueDate before BillDate.", nameof(req));

        var vendor = await _db.Vendors.FirstOrDefaultAsync(v => v.VendorId == req.VendorId && v.TenantId == req.TenantId, ct)
            ?? throw new ApException($"Vendor {req.VendorId} not found.");

        var rates = GhanaTaxRates.ForDate(req.BillDate);

        long net = 0, levies = 0, vat = 0, gross = 0;
        var bill = new ApBill
        {
            VendorId = vendor.VendorId,
            VendorReference = req.VendorReference.Trim(),
            BillDate = req.BillDate,
            DueDate = req.DueDate,
            PoReference = req.PoReference,
            GrnReference = req.GrnReference,
            CurrencyCode = req.CurrencyCode,
            Status = ApBillStatus.Captured,
            CreatedAt = _clock.GetUtcNow(),
            Notes = req.Notes,
            TenantId = req.TenantId
        };

        short n = 1;
        var defaultExp = vendor.DefaultExpenseAccount ?? "5040";
        foreach (var l in req.Lines)
        {
            if (l.AmountMinor <= 0) throw new ArgumentException("Line amount must be positive.", nameof(req));
            long lineNet, lineLevies, lineVat, lineGross;

            switch (req.TaxTreatment)
            {
                case BillTaxTreatment.PreTax:
                    var tFwd = TaxCalculator.FromNet(new Money(l.AmountMinor, req.CurrencyCode), rates);
                    lineNet = tFwd.Net.Minor; lineLevies = tFwd.TotalLevies.Minor;
                    lineVat = tFwd.Vat.Minor; lineGross = tFwd.Gross.Minor;
                    break;
                case BillTaxTreatment.GhanaInclusive:
                    var tBack = TaxCalculator.FromGross(new Money(l.AmountMinor, req.CurrencyCode), rates);
                    lineNet = tBack.Net.Minor; lineLevies = tBack.TotalLevies.Minor;
                    lineVat = tBack.Vat.Minor; lineGross = tBack.Gross.Minor;
                    break;
                default: // None
                    lineNet = l.AmountMinor; lineLevies = 0; lineVat = 0; lineGross = l.AmountMinor;
                    break;
            }

            checked { net += lineNet; levies += lineLevies; vat += lineVat; gross += lineGross; }

            bill.Lines.Add(new ApBillLine
            {
                ApBillId = bill.ApBillId,
                LineNo = n++,
                Description = l.Description,
                NetAmountMinor = lineNet,
                ExpenseAccount = string.IsNullOrWhiteSpace(l.ExpenseAccountOverride) ? defaultExp : l.ExpenseAccountOverride!.Trim(),
                CurrencyCode = req.CurrencyCode
            });
        }

        bill.SubtotalNetMinor = net;
        bill.LeviesMinor = levies;
        bill.VatMinor = vat;
        bill.GrossMinor = gross;
        bill.BillNo = await GenerateBillNoAsync(req.BillDate, req.TenantId, ct);
        _db.Bills.Add(bill);
        await _db.SaveChangesAsync(ct);
        return bill;
    }

    // -----------------------------------------------------------------
    // Approve (post journal)
    // -----------------------------------------------------------------

    public async Task<ApBill> ApproveBillAsync(Guid billId, Guid approverUserId, DateOnly effectiveDate, Guid periodId, CancellationToken ct = default)
    {
        var bill = await _db.Bills.Include(b => b.Lines).FirstOrDefaultAsync(b => b.ApBillId == billId, ct)
            ?? throw new ApException($"Bill {billId} not found.");
        if (bill.Status != ApBillStatus.Captured) throw new ApException($"Cannot approve a bill in state {bill.Status}.");

        var ev = new LedgerEvent
        {
            TenantId = bill.TenantId,
            EffectiveDate = effectiveDate,
            PeriodId = periodId,
            SourceModule = "ap",
            SourceEntityType = "ApBill",
            SourceEntityId = bill.ApBillId.ToString("N"),
            IdempotencyKey = $"ap:{bill.ApBillId:N}:approve",
            Narration = $"AP bill {bill.BillNo} approved",
            ActorUserId = approverUserId
        };
        short ln = 1;
        foreach (var l in bill.Lines.OrderBy(l => l.LineNo))
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = l.ExpenseAccount,
                DebitMinor = l.NetAmountMinor, CurrencyCode = l.CurrencyCode,
                Description = l.Description
            });
        }
        if (bill.VatMinor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = "1410", DebitMinor = bill.VatMinor,
                CurrencyCode = bill.CurrencyCode, Description = $"VAT input on {bill.BillNo}"
            });
        }
        if (bill.LeviesMinor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = bill.Lines.First().ExpenseAccount,
                DebitMinor = bill.LeviesMinor, CurrencyCode = bill.CurrencyCode,
                Description = $"NHIL/GETFund/COVID levies on {bill.BillNo}"
            });
        }
        ev.Lines.Add(new LedgerEventLine
        {
            LineNo = ln, AccountCode = "2000", CreditMinor = bill.GrossMinor,
            CurrencyCode = bill.CurrencyCode, Description = $"AP — {bill.BillNo}"
        });

        var eventId = await _ledger.PostAsync(ev, ct);
        bill.LedgerEventId = eventId;
        bill.Status = ApBillStatus.Approved;
        bill.ApprovedAt = _clock.GetUtcNow();
        bill.ApprovedByUserId = approverUserId;
        await _db.SaveChangesAsync(ct);
        return bill;
    }

    // -----------------------------------------------------------------
    // Pay (with WHT)
    // -----------------------------------------------------------------

    public async Task<(ApPayment Payment, WhtCertificate? Certificate)> PayBillAsync(PayBillRequest req, Guid periodId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.AmountMinor <= 0) throw new ArgumentException("Amount must be positive.", nameof(req));

        var bill = await _db.Bills.FirstOrDefaultAsync(b => b.ApBillId == req.BillId && b.TenantId == req.TenantId, ct)
            ?? throw new ApException($"Bill {req.BillId} not found.");
        if (bill.Status is ApBillStatus.Captured or ApBillStatus.Void)
            throw new ApException($"Cannot pay a bill in state {bill.Status}.");
        if (req.AmountMinor > bill.OutstandingMinor)
            throw new ApException($"Payment {req.AmountMinor} exceeds outstanding {bill.OutstandingMinor}.");

        var vendor = await _db.Vendors.FirstAsync(v => v.VendorId == bill.VendorId, ct);

        // WHT — only deducted if bill is approved + vendor not exempt + WHT type isn't Exempt.
        long whtDeducted = 0;
        decimal whtRate = 0m;
        WhtTransactionType whtType = vendor.DefaultWht;
        if (!vendor.WhtExempt && whtType != WhtTransactionType.Exempt)
        {
            var w = WithholdingTax.Compute(new Money(req.AmountMinor, bill.CurrencyCode), whtType, vendor.IsVatRegistered);
            whtDeducted = w.WhtDeducted.Minor;
            whtRate = w.Rate;
        }

        var ev = new LedgerEvent
        {
            TenantId = bill.TenantId,
            EffectiveDate = req.PaymentDate,
            PeriodId = periodId,
            SourceModule = "ap",
            SourceEntityType = "ApPayment",
            SourceEntityId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"ap:{bill.ApBillId:N}:pay:{Guid.NewGuid():N}",
            Narration = $"AP payment for bill {bill.BillNo} via {req.PaymentRail}",
            ActorUserId = req.RecordedByUserId,
            Lines =
            {
                new LedgerEventLine { LineNo = 1, AccountCode = "2000",
                    DebitMinor = req.AmountMinor, CurrencyCode = bill.CurrencyCode,
                    Description = $"AP settled — {bill.BillNo}" }
            }
        };
        short l = 2;
        if (whtDeducted > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = l++, AccountCode = "2150",
                CreditMinor = whtDeducted, CurrencyCode = bill.CurrencyCode,
                Description = $"WHT @ {whtRate:P1} on {bill.BillNo}"
            });
        }
        ev.Lines.Add(new LedgerEventLine
        {
            LineNo = l, AccountCode = req.CashAccount,
            CreditMinor = req.AmountMinor - whtDeducted, CurrencyCode = bill.CurrencyCode,
            Description = $"Pay {bill.BillNo} via {req.PaymentRail} ({req.RailReference ?? "no ref"})"
        });
        var eventId = await _ledger.PostAsync(ev, ct);

        var payment = new ApPayment
        {
            ApBillId = bill.ApBillId,
            PaymentDate = req.PaymentDate,
            AmountMinor = req.AmountMinor,
            CurrencyCode = bill.CurrencyCode,
            PaymentRail = req.PaymentRail,
            CashAccount = req.CashAccount,
            RailReference = req.RailReference,
            PaymentRunId = req.PaymentRunId,
            RecordedByUserId = req.RecordedByUserId,
            RecordedAt = _clock.GetUtcNow(),
            LedgerEventId = eventId,
            TenantId = bill.TenantId
        };
        _db.Payments.Add(payment);
        bill.PaidMinor = checked(bill.PaidMinor + req.AmountMinor);
        bill.WhtMinor = checked(bill.WhtMinor + whtDeducted);
        bill.WhtRateBp = (long)Math.Round(whtRate * 10_000m, 0);
        bill.Status = bill.PaidMinor >= bill.GrossMinor ? ApBillStatus.Paid : ApBillStatus.PartiallyPaid;
        bill.PaidAt = bill.Status == ApBillStatus.Paid ? _clock.GetUtcNow() : bill.PaidAt;

        WhtCertificate? cert = null;
        if (whtDeducted > 0)
        {
            cert = new WhtCertificate
            {
                CertificateNo = await GenerateWhtCertNoAsync(req.PaymentDate, bill.TenantId, ct),
                VendorId = vendor.VendorId,
                ApBillId = bill.ApBillId,
                ApPaymentId = payment.ApPaymentId,
                IssueDate = req.PaymentDate,
                GrossPaidMinor = req.AmountMinor,
                WhtDeductedMinor = whtDeducted,
                WhtRate = whtRate,
                TransactionType = whtType,
                CurrencyCode = bill.CurrencyCode,
                CreatedAt = _clock.GetUtcNow(),
                TenantId = bill.TenantId
            };
            _db.WhtCertificates.Add(cert);
        }

        await _db.SaveChangesAsync(ct);
        return (payment, cert);
    }

    public async Task<ApBill> VoidBillAsync(Guid billId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason required.", nameof(reason));
        var bill = await _db.Bills.FirstOrDefaultAsync(b => b.ApBillId == billId, ct)
            ?? throw new ApException($"Bill {billId} not found.");
        if (bill.Status is ApBillStatus.Paid or ApBillStatus.Void)
            throw new ApException($"Cannot void a bill in state {bill.Status}.");
        bill.Status = ApBillStatus.Void;
        bill.Notes = $"{bill.Notes}\nVoided ({_clock.GetUtcNow():u}) by {actorUserId}: {reason}";
        await _db.SaveChangesAsync(ct);
        return bill;
    }

    public async Task<IReadOnlyList<AgingBucket>> AgingReportAsync(DateOnly asOf, long tenantId = 1, CancellationToken ct = default)
    {
        var open = await _db.Bills
            .Where(b => b.TenantId == tenantId && (b.Status == ApBillStatus.Approved || b.Status == ApBillStatus.PartiallyPaid))
            .ToListAsync(ct);
        var buckets = new (string, int, int)[] { ("0-30", 0, 30), ("31-60", 31, 60), ("61-90", 61, 90), ("90+", 91, int.MaxValue) };
        var result = new List<AgingBucket>(buckets.Length);
        foreach (var (label, min, max) in buckets)
        {
            var matching = open.Where(b =>
            {
                var d = asOf.DayNumber - b.DueDate.DayNumber;
                if (d < 0) d = 0;
                return d >= min && d <= max;
            }).ToList();
            var ccy = matching.Count > 0 ? matching[0].CurrencyCode : "GHS";
            result.Add(new AgingBucket(label, matching.Count, new Money(matching.Sum(b => b.OutstandingMinor), ccy)));
        }
        return result;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<string> GenerateBillNoAsync(DateOnly d, long tenantId, CancellationToken ct)
    {
        var prefix = $"AP-{d:yyyy-MM}-";
        var taken = await _db.Bills.CountAsync(b => b.TenantId == tenantId && b.BillNo.StartsWith(prefix), ct);
        return $"{prefix}{(taken + 1):D5}";
    }

    private async Task<string> GenerateWhtCertNoAsync(DateOnly d, long tenantId, CancellationToken ct)
    {
        var prefix = $"WHT-{d:yyyy-MM}-";
        var taken = await _db.WhtCertificates.CountAsync(c => c.TenantId == tenantId && c.CertificateNo.StartsWith(prefix), ct);
        return $"{prefix}{(taken + 1):D5}";
    }
}
