using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;
using NickFinance.Ledger;
using NickFinance.TaxEngine;

namespace NickFinance.AR;

/// <summary>
/// Module-facing API for Accounts Receivable. Customer master + invoice
/// state machine + receipts. The flow:
/// <list type="number">
///   <item><description><see cref="CreateCustomerAsync"/> — provisions the master record (idempotent on Code).</description></item>
///   <item><description><see cref="DraftInvoiceAsync"/> — creates a Draft invoice with one or more lines (net amounts).</description></item>
///   <item><description><see cref="IssueInvoiceAsync"/> — computes Ghana taxes via <see cref="TaxCalculator"/>, calls <see cref="IEvatProvider"/> for the IRN, posts the journal (DR 1100 / CR 4xxx + tax payable splits), transitions to <see cref="ArInvoiceStatus.Issued"/>.</description></item>
///   <item><description><see cref="RecordReceiptAsync"/> — books the cash, posts (DR 1030 / CR 1100) journal, advances status to PartiallyPaid or Paid as the cumulative receipts cross the gross.</description></item>
///   <item><description><see cref="ScanCompletedAsync"/> — auto-creates a Draft invoice from a scan-completion payload; production wires this into the NSCIM event bus when Phase 4 lands.</description></item>
/// </list>
/// </summary>
public interface IArService
{
    Task<Customer> CreateCustomerAsync(CreateCustomerRequest req, CancellationToken ct = default);

    Task<ArInvoice> DraftInvoiceAsync(DraftInvoiceRequest req, CancellationToken ct = default);

    Task<ArInvoice> IssueInvoiceAsync(Guid invoiceId, Guid actorUserId, DateOnly effectiveDate, Guid periodId, CancellationToken ct = default);

    Task<ArReceipt> RecordReceiptAsync(RecordReceiptRequest req, Guid periodId, CancellationToken ct = default);

    Task<ArInvoice> VoidInvoiceAsync(Guid invoiceId, Guid actorUserId, string reason, CancellationToken ct = default);

    /// <summary>Auto-draft an invoice from a scan-completion event. Idempotent on (declaration, customer).</summary>
    Task<ArInvoice> ScanCompletedAsync(ScanCompletedEvent ev, CancellationToken ct = default);

    Task<IReadOnlyList<AgingBucket>> AgingReportAsync(DateOnly asOf, long tenantId = 1, CancellationToken ct = default);
}

public sealed record CreateCustomerRequest(
    string Code,
    string Name,
    string? Tin = null,
    bool IsVatRegistered = false,
    string? Email = null,
    string? Phone = null,
    string? Address = null,
    string? ArControlAccount = null,
    long TenantId = 1);

public sealed record DraftInvoiceRequest(
    Guid CustomerId,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string? Reference,
    IReadOnlyList<DraftInvoiceLine> Lines,
    string CurrencyCode = "GHS",
    string SourceModule = "manual",
    string? SourceEntityId = null,
    long TenantId = 1);

public sealed record DraftInvoiceLine(
    string Description,
    long NetAmountMinor,
    string RevenueAccount = "4010");

public sealed record RecordReceiptRequest(
    Guid InvoiceId,
    DateOnly ReceiptDate,
    long AmountMinor,
    string CashAccount,
    Guid RecordedByUserId,
    string? Reference = null,
    long TenantId = 1);

public sealed record ScanCompletedEvent(
    string DeclarationNumber,
    Guid CustomerId,
    long FeeNetMinor,
    string CurrencyCode,
    DateOnly CompletedOn,
    long TenantId = 1);

public sealed record AgingBucket(
    string Bucket,
    int InvoiceCount,
    Money OutstandingTotal);

public sealed class ArException : Exception
{
    public ArException(string message) : base(message) { }
}

public sealed class ArService : IArService
{
    private readonly ArDbContext _db;
    private readonly ILedgerWriter _ledger;
    private readonly IEvatProvider _evat;
    private readonly ISecurityAuditService _audit;
    private readonly TimeProvider _clock;

    public ArService(ArDbContext db, ILedgerWriter ledger, IEvatProvider? evat = null, TimeProvider? clock = null)
        : this(db, ledger, evat, audit: null, clock) { }

    public ArService(ArDbContext db, ILedgerWriter ledger, IEvatProvider? evat, ISecurityAuditService? audit, TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _evat = evat ?? new StubEvatProvider();
        _audit = audit ?? new NoopSecurityAuditService();
        _clock = clock ?? TimeProvider.System;
    }

    // -----------------------------------------------------------------
    // Customer
    // -----------------------------------------------------------------

    public async Task<Customer> CreateCustomerAsync(CreateCustomerRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (string.IsNullOrWhiteSpace(req.Code)) throw new ArgumentException("Code is required.", nameof(req));
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("Name is required.", nameof(req));

        var existing = await _db.Customers
            .FirstOrDefaultAsync(c => c.TenantId == req.TenantId && c.Code == req.Code, ct);
        if (existing is not null) return existing;

        var now = _clock.GetUtcNow();
        var c = new Customer
        {
            Code = req.Code.Trim(),
            Name = req.Name.Trim(),
            Tin = req.Tin?.Trim(),
            IsVatRegistered = req.IsVatRegistered,
            Email = req.Email,
            Phone = req.Phone,
            Address = req.Address,
            ArControlAccount = req.ArControlAccount,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            TenantId = req.TenantId
        };
        _db.Customers.Add(c);
        await _db.SaveChangesAsync(ct);
        return c;
    }

    // -----------------------------------------------------------------
    // Invoice draft
    // -----------------------------------------------------------------

    public async Task<ArInvoice> DraftInvoiceAsync(DraftInvoiceRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Lines is null || req.Lines.Count == 0) throw new ArgumentException("At least one line item is required.", nameof(req));
        if (req.DueDate < req.InvoiceDate) throw new ArgumentException("DueDate is before InvoiceDate.", nameof(req));

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == req.CustomerId && c.TenantId == req.TenantId, ct)
            ?? throw new ArException($"Customer {req.CustomerId} not found.");

        long subtotal = 0;
        foreach (var l in req.Lines)
        {
            if (l.NetAmountMinor <= 0) throw new ArgumentException("Line net amount must be positive.", nameof(req));
            checked { subtotal += l.NetAmountMinor; }
        }

        var inv = new ArInvoice
        {
            CustomerId = customer.CustomerId,
            CurrencyCode = req.CurrencyCode,
            InvoiceDate = req.InvoiceDate,
            DueDate = req.DueDate,
            Reference = req.Reference,
            Status = ArInvoiceStatus.Draft,
            SubtotalNetMinor = subtotal,
            // Levies + VAT computed on Issue, not Draft, so the rate set used
            // is the one in force at issue time.
            CreatedAt = _clock.GetUtcNow(),
            SourceModule = req.SourceModule,
            SourceEntityId = req.SourceEntityId,
            TenantId = req.TenantId
        };

        short n = 1;
        foreach (var l in req.Lines)
        {
            inv.Lines.Add(new ArInvoiceLine
            {
                ArInvoiceId = inv.ArInvoiceId,
                LineNo = n++,
                Description = l.Description,
                NetAmountMinor = l.NetAmountMinor,
                RevenueAccount = l.RevenueAccount,
                CurrencyCode = req.CurrencyCode
            });
        }

        _db.Invoices.Add(inv);
        await _db.SaveChangesAsync(ct);
        return inv;
    }

    // -----------------------------------------------------------------
    // Invoice issue
    // -----------------------------------------------------------------

    public async Task<ArInvoice> IssueInvoiceAsync(Guid invoiceId, Guid actorUserId, DateOnly effectiveDate, Guid periodId, CancellationToken ct = default)
    {
        var inv = await _db.Invoices.Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.ArInvoiceId == invoiceId, ct)
            ?? throw new ArException($"Invoice {invoiceId} not found.");
        if (inv.Status != ArInvoiceStatus.Draft)
        {
            throw new ArException($"Cannot issue an invoice in state {inv.Status}.");
        }

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == inv.CustomerId, ct)
            ?? throw new ArException("Customer disappeared between draft and issue.");

        // Ghana tax computation from the line nets.
        var rates = GhanaTaxRates.ForDate(effectiveDate);
        var net = new Money(inv.SubtotalNetMinor, inv.CurrencyCode);
        var tax = TaxCalculator.FromNet(net, rates);
        inv.LeviesMinor = tax.TotalLevies.Minor;
        inv.VatMinor = tax.Vat.Minor;
        inv.GrossMinor = tax.Gross.Minor;

        inv.InvoiceNo = await GenerateInvoiceNoAsync(effectiveDate, inv.TenantId, ct);

        // 1. Get the IRN from GRA partner.
        var evat = await _evat.IssueAsync(new EvatIssueRequest(
            inv.ArInvoiceId, inv.InvoiceNo, customer.Name, customer.Tin,
            effectiveDate, inv.CurrencyCode,
            inv.SubtotalNetMinor, inv.LeviesMinor, inv.VatMinor, inv.GrossMinor,
            inv.TenantId), ct);
        if (!evat.Accepted)
        {
            throw new ArException($"e-VAT issuance via {_evat.Provider} failed: {evat.FailureReason ?? "(no reason)"}.");
        }
        inv.EvatIrn = evat.Irn;
        inv.EvatIssuedAt = _clock.GetUtcNow();

        // 2. Post the journal:
        //   DR  1100 Trade receivables — control     [gross]
        //   CR  4xxx Revenue per line                [each line's net]
        //   CR  2110 VAT output payable              [vat]
        //   CR  2120 NHIL payable                    [nhil]
        //   CR  2130 GETFund Levy payable            [getfund]
        //   CR  2140 COVID levy payable              [covid]
        var arAccount = customer.ArControlAccount ?? "1100";
        var ev = new LedgerEvent
        {
            TenantId = inv.TenantId,
            EffectiveDate = effectiveDate,
            PeriodId = periodId,
            SourceModule = "ar",
            SourceEntityType = "ArInvoice",
            SourceEntityId = inv.ArInvoiceId.ToString("N"),
            IdempotencyKey = $"ar:{inv.ArInvoiceId:N}:issue",
            EventType = LedgerEventType.Posted,
            Narration = $"AR invoice {inv.InvoiceNo} issued for {customer.Name}",
            ActorUserId = actorUserId
        };
        short ln = 1;
        ev.Lines.Add(new LedgerEventLine
        {
            LineNo = ln++, AccountCode = arAccount,
            DebitMinor = inv.GrossMinor, CurrencyCode = inv.CurrencyCode,
            Description = $"AR — {customer.Name} {inv.InvoiceNo}"
        });
        foreach (var line in inv.Lines.OrderBy(l => l.LineNo))
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = line.RevenueAccount,
                CreditMinor = line.NetAmountMinor, CurrencyCode = line.CurrencyCode,
                Description = line.Description
            });
        }
        if (tax.Vat.Minor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = "2110",
                CreditMinor = tax.Vat.Minor, CurrencyCode = inv.CurrencyCode,
                Description = $"VAT output on {inv.InvoiceNo}"
            });
        }
        if (tax.Nhil.Minor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = "2120",
                CreditMinor = tax.Nhil.Minor, CurrencyCode = inv.CurrencyCode,
                Description = $"NHIL on {inv.InvoiceNo}"
            });
        }
        if (tax.GetFund.Minor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln++, AccountCode = "2130",
                CreditMinor = tax.GetFund.Minor, CurrencyCode = inv.CurrencyCode,
                Description = $"GETFund on {inv.InvoiceNo}"
            });
        }
        if (tax.Covid.Minor > 0)
        {
            ev.Lines.Add(new LedgerEventLine
            {
                LineNo = ln, AccountCode = "2140",
                CreditMinor = tax.Covid.Minor, CurrencyCode = inv.CurrencyCode,
                Description = $"COVID levy on {inv.InvoiceNo}"
            });
        }

        var eventId = await _ledger.PostAsync(ev, ct);
        inv.LedgerEventId = eventId;
        inv.Status = ArInvoiceStatus.Issued;
        inv.IssuedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: SecurityAuditAction.InvoiceIssued,
            targetType: "ArInvoice",
            targetId: inv.ArInvoiceId.ToString(),
            result: SecurityAuditResult.Allowed,
            details: new { invoiceNo = inv.InvoiceNo, customerId = inv.CustomerId, grossMinor = inv.GrossMinor, evatIrn = inv.EvatIrn },
            ct: ct);
        return inv;
    }

    // -----------------------------------------------------------------
    // Receipt
    // -----------------------------------------------------------------

    public async Task<ArReceipt> RecordReceiptAsync(RecordReceiptRequest req, Guid periodId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.AmountMinor <= 0) throw new ArgumentException("Amount must be positive.", nameof(req));

        var inv = await _db.Invoices.FirstOrDefaultAsync(i => i.ArInvoiceId == req.InvoiceId && i.TenantId == req.TenantId, ct)
            ?? throw new ArException($"Invoice {req.InvoiceId} not found.");
        if (inv.Status is ArInvoiceStatus.Draft or ArInvoiceStatus.Void)
        {
            throw new ArException($"Cannot receipt an invoice in state {inv.Status}.");
        }
        if (inv.OutstandingMinor <= 0)
        {
            throw new ArException("Invoice is fully paid.");
        }
        if (req.AmountMinor > inv.OutstandingMinor)
        {
            throw new ArException($"Receipt {req.AmountMinor} exceeds outstanding {inv.OutstandingMinor}.");
        }
        if (!string.Equals(req.CashAccount, "1010", StringComparison.Ordinal)
            && !req.CashAccount.StartsWith("103", StringComparison.Ordinal)
            && !req.CashAccount.StartsWith("102", StringComparison.Ordinal))
        {
            // Soft-validation; we don't have a CoA dependency here. Real
            // wiring should run req.CashAccount through ICoaService.IsActiveAsync.
        }

        var arAccount = (await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == inv.CustomerId, ct))?.ArControlAccount ?? "1100";

        var ev = new LedgerEvent
        {
            TenantId = inv.TenantId,
            EffectiveDate = req.ReceiptDate,
            PeriodId = periodId,
            SourceModule = "ar",
            SourceEntityType = "ArReceipt",
            SourceEntityId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = $"ar:{inv.ArInvoiceId:N}:receipt:{Guid.NewGuid():N}",
            EventType = LedgerEventType.Posted,
            Narration = $"Receipt against invoice {inv.InvoiceNo}",
            ActorUserId = req.RecordedByUserId,
            Lines =
            {
                new LedgerEventLine { LineNo = 1, AccountCode = req.CashAccount,
                    DebitMinor = req.AmountMinor, CurrencyCode = inv.CurrencyCode,
                    Description = $"Receipt — {inv.InvoiceNo} ({req.Reference ?? "no ref"})" },
                new LedgerEventLine { LineNo = 2, AccountCode = arAccount,
                    CreditMinor = req.AmountMinor, CurrencyCode = inv.CurrencyCode,
                    Description = $"AR settled — {inv.InvoiceNo}" }
            }
        };
        var eventId = await _ledger.PostAsync(ev, ct);

        var receipt = new ArReceipt
        {
            ArInvoiceId = inv.ArInvoiceId,
            ReceiptDate = req.ReceiptDate,
            AmountMinor = req.AmountMinor,
            CurrencyCode = inv.CurrencyCode,
            CashAccount = req.CashAccount,
            Reference = req.Reference,
            RecordedByUserId = req.RecordedByUserId,
            RecordedAt = _clock.GetUtcNow(),
            LedgerEventId = eventId,
            TenantId = inv.TenantId
        };
        _db.Receipts.Add(receipt);

        inv.PaidMinor = checked(inv.PaidMinor + req.AmountMinor);
        inv.Status = inv.PaidMinor >= inv.GrossMinor
            ? ArInvoiceStatus.Paid
            : ArInvoiceStatus.PartiallyPaid;

        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: SecurityAuditAction.ReceiptRecorded,
            targetType: "ArInvoice",
            targetId: inv.ArInvoiceId.ToString(),
            result: SecurityAuditResult.Allowed,
            details: new { invoiceNo = inv.InvoiceNo, amountMinor = req.AmountMinor, ledgerEventId = eventId, cashAccount = req.CashAccount },
            ct: ct);
        return receipt;
    }

    // -----------------------------------------------------------------
    // Void
    // -----------------------------------------------------------------

    public async Task<ArInvoice> VoidInvoiceAsync(Guid invoiceId, Guid actorUserId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Reason is required.", nameof(reason));
        var inv = await _db.Invoices.FirstOrDefaultAsync(i => i.ArInvoiceId == invoiceId, ct)
            ?? throw new ArException($"Invoice {invoiceId} not found.");
        if (inv.Status is ArInvoiceStatus.Paid or ArInvoiceStatus.Void)
        {
            throw new ArException($"Cannot void an invoice in state {inv.Status}.");
        }
        // For Issued invoices, the actual ledger reversal is the caller's
        // responsibility (typically via ILedgerWriter.ReverseAsync) — we
        // just mark the row Void here so it stops appearing in receivables.
        inv.Status = ArInvoiceStatus.Void;
        inv.VoidedAt = _clock.GetUtcNow();
        inv.VoidReason = reason.Trim();
        _ = actorUserId;     // recorded via actor field on the reversal journal when caller posts it
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: SecurityAuditAction.InvoiceVoided,
            targetType: "ArInvoice",
            targetId: inv.ArInvoiceId.ToString(),
            result: SecurityAuditResult.Allowed,
            details: new { invoiceNo = inv.InvoiceNo, reason = inv.VoidReason },
            ct: ct);
        return inv;
    }

    // -----------------------------------------------------------------
    // Scan to invoice
    // -----------------------------------------------------------------

    public async Task<ArInvoice> ScanCompletedAsync(ScanCompletedEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.FeeNetMinor <= 0) throw new ArgumentException("FeeNetMinor must be positive.", nameof(ev));

        // Idempotency: don't re-create an invoice for the same (declaration, customer).
        var existing = await _db.Invoices
            .Where(i => i.TenantId == ev.TenantId && i.SourceModule == "scan_to_invoice" && i.SourceEntityId == ev.DeclarationNumber)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.CustomerId == ev.CustomerId && c.TenantId == ev.TenantId, ct)
            ?? throw new ArException($"Unknown customer {ev.CustomerId}.");

        return await DraftInvoiceAsync(new DraftInvoiceRequest(
            customer.CustomerId,
            ev.CompletedOn,
            ev.CompletedOn.AddDays(30),
            Reference: $"Scan {ev.DeclarationNumber}",
            Lines: new[]
            {
                new DraftInvoiceLine($"Scan service — declaration {ev.DeclarationNumber}", ev.FeeNetMinor, "4010")
            },
            CurrencyCode: ev.CurrencyCode,
            SourceModule: "scan_to_invoice",
            SourceEntityId: ev.DeclarationNumber,
            TenantId: ev.TenantId), ct);
    }

    // -----------------------------------------------------------------
    // Aging
    // -----------------------------------------------------------------

    public async Task<IReadOnlyList<AgingBucket>> AgingReportAsync(DateOnly asOf, long tenantId = 1, CancellationToken ct = default)
    {
        var open = await _db.Invoices
            .Where(i => i.TenantId == tenantId
                     && (i.Status == ArInvoiceStatus.Issued || i.Status == ArInvoiceStatus.PartiallyPaid))
            .ToListAsync(ct);

        var buckets = new (string Label, int MinDays, int MaxDays)[]
        {
            ("0-30",   0,   30),
            ("31-60",  31,  60),
            ("61-90",  61,  90),
            ("90+",    91,  int.MaxValue)
        };
        var result = new List<AgingBucket>(buckets.Length);
        foreach (var b in buckets)
        {
            var matching = open.Where(i =>
            {
                var daysOverdue = asOf.DayNumber - i.DueDate.DayNumber;
                if (daysOverdue < 0) daysOverdue = 0;
                return daysOverdue >= b.MinDays && daysOverdue <= b.MaxDays;
            }).ToList();

            var ccy = matching.Count > 0 ? matching[0].CurrencyCode : "GHS";
            var sum = matching.Sum(i => i.OutstandingMinor);
            result.Add(new AgingBucket(b.Label, matching.Count, new Money(sum, ccy)));
        }
        return result;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<string> GenerateInvoiceNoAsync(DateOnly date, long tenantId, CancellationToken ct)
    {
        var prefix = $"INV-{date:yyyy-MM}-";
        var taken = await _db.Invoices.CountAsync(
            i => i.TenantId == tenantId && i.InvoiceNo.StartsWith(prefix), ct);
        return $"{prefix}{(taken + 1):D5}";
    }
}
