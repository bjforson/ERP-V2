namespace NickFinance.AR;

// ---------------------------------------------------------------------------
// Customer master
// ---------------------------------------------------------------------------

public class Customer
{
    public Guid CustomerId { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly customer code, e.g. <c>"AGRO-IMP-001"</c>.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Tax Identification Number (Ghana — usually the Ghana Card PIN).</summary>
    public string? Tin { get; set; }

    /// <summary>Whether the customer is registered for VAT — drives WHT applicability the customer enforces on their side.</summary>
    public bool IsVatRegistered { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }

    /// <summary>Optional override for the AR control account; defaults to <c>1100 Trade receivables</c> when null.</summary>
    public string? ArControlAccount { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public long TenantId { get; set; } = 1;
}

// ---------------------------------------------------------------------------
// Invoice
// ---------------------------------------------------------------------------

public class ArInvoice
{
    public Guid ArInvoiceId { get; set; } = Guid.NewGuid();

    /// <summary>Human-friendly invoice number, e.g. <c>"INV-2026-04-00001"</c>. Generated on Issue.</summary>
    public string InvoiceNo { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }

    public string CurrencyCode { get; set; } = "GHS";
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }

    /// <summary>Free-text purpose / customer reference / scan declaration number.</summary>
    public string? Reference { get; set; }

    public ArInvoiceStatus Status { get; set; } = ArInvoiceStatus.Draft;

    /// <summary>The GRA e-VAT IRN (Invoice Reference Number) issued by the partner. Set on Issue.</summary>
    public string? EvatIrn { get; set; }
    public DateTimeOffset? EvatIssuedAt { get; set; }

    /// <summary>Net subtotal (sum of line nets) in minor units.</summary>
    public long SubtotalNetMinor { get; set; }

    /// <summary>Total levies (NHIL+GETFund+COVID) in minor units.</summary>
    public long LeviesMinor { get; set; }

    /// <summary>VAT in minor units.</summary>
    public long VatMinor { get; set; }

    /// <summary>Gross total (= SubtotalNet + Levies + VAT).</summary>
    public long GrossMinor { get; set; }

    /// <summary>Sum of receipts so far.</summary>
    public long PaidMinor { get; set; }

    /// <summary>Convenience — gross minus paid, in minor units.</summary>
    public long OutstandingMinor => GrossMinor - PaidMinor;

    /// <summary>The Ledger event id posted on Issue. Null until Issued.</summary>
    public Guid? LedgerEventId { get; set; }

    /// <summary>Originating module — <c>"manual"</c>, <c>"scan_to_invoice"</c>, etc.</summary>
    public string SourceModule { get; set; } = "manual";
    public string? SourceEntityId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidReason { get; set; }

    public long TenantId { get; set; } = 1;

    public List<ArInvoiceLine> Lines { get; set; } = new();
}

public class ArInvoiceLine
{
    public Guid ArInvoiceLineId { get; set; } = Guid.NewGuid();
    public Guid ArInvoiceId { get; set; }

    public short LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>Net (pre-tax) amount in minor units. Levy + VAT computed on Issue.</summary>
    public long NetAmountMinor { get; set; }

    /// <summary>Revenue GL account this line credits (e.g. <c>4010</c> scan revenue).</summary>
    public string RevenueAccount { get; set; } = "4010";

    public string CurrencyCode { get; set; } = "GHS";
}

public enum ArInvoiceStatus
{
    Draft = 0,
    Issued = 1,
    PartiallyPaid = 2,
    Paid = 3,
    Void = 4
}

// ---------------------------------------------------------------------------
// Receipt (payment from customer)
// ---------------------------------------------------------------------------

public class ArReceipt
{
    public Guid ArReceiptId { get; set; } = Guid.NewGuid();
    public Guid ArInvoiceId { get; set; }

    public DateOnly ReceiptDate { get; set; }
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "GHS";

    /// <summary>The cash account credited on receipt — <c>1030</c> bank, <c>1020</c> MoMo, etc.</summary>
    public string CashAccount { get; set; } = "1030";

    /// <summary>Free-text bank/MoMo reference for audit.</summary>
    public string? Reference { get; set; }

    public Guid RecordedByUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>The Ledger event id this receipt posted.</summary>
    public Guid? LedgerEventId { get; set; }

    public long TenantId { get; set; } = 1;
}
