using NickFinance.TaxEngine;

namespace NickFinance.AP;

/// <summary>One vendor/supplier the business pays.</summary>
public class Vendor
{
    public Guid VendorId { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Ghana Card PIN or other TIN. Required for vendors paid above the WHT threshold.</summary>
    public string? Tin { get; set; }
    public bool IsVatRegistered { get; set; }
    /// <summary>Default <see cref="WhtTransactionType"/> for bills from this vendor — usually <c>SupplyOfGoods</c> or <c>SupplyOfServices</c>.</summary>
    public WhtTransactionType DefaultWht { get; set; } = WhtTransactionType.SupplyOfServices;
    public bool WhtExempt { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    /// <summary>MoMo number for payment runs.</summary>
    public string? MomoNumber { get; set; }
    public string? MomoNetwork { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankName { get; set; }
    /// <summary>Default <see cref="Ledger.LedgerEventLine.AccountCode"/> credited when receipting bills (e.g. <c>5040</c> sub-contractor).</summary>
    public string? DefaultExpenseAccount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long TenantId { get; set; } = 1;
}

/// <summary>A bill received from a vendor — the unit of payment.</summary>
public class ApBill
{
    public Guid ApBillId { get; set; } = Guid.NewGuid();
    public string BillNo { get; set; } = string.Empty;        // human friendly e.g. "AP-2026-04-00012"
    public string VendorReference { get; set; } = string.Empty;  // vendor's invoice number
    public Guid VendorId { get; set; }
    public DateOnly BillDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string? PoReference { get; set; }                  // optional PO link for 3-way match
    public string? GrnReference { get; set; }                 // optional GRN link
    public string CurrencyCode { get; set; } = "GHS";
    public ApBillStatus Status { get; set; } = ApBillStatus.Captured;

    /// <summary>Net + tax breakdown computed at capture (Ghana inclusive bills are back-solved).</summary>
    public long SubtotalNetMinor { get; set; }
    public long LeviesMinor { get; set; }
    public long VatMinor { get; set; }
    public long GrossMinor { get; set; }

    /// <summary>WHT computed at <em>payment</em> time (deducted from supplier net).</summary>
    public long WhtRateBp { get; set; }      // basis points * 100, e.g. 750 = 7.5%
    public long WhtMinor { get; set; }
    public long PaidMinor { get; set; }
    public long OutstandingMinor => GrossMinor - PaidMinor;

    public string? Notes { get; set; }
    public Guid? LedgerEventId { get; set; }   // posted on Approve
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public long TenantId { get; set; } = 1;
    public List<ApBillLine> Lines { get; set; } = new();
}

public class ApBillLine
{
    public Guid ApBillLineId { get; set; } = Guid.NewGuid();
    public Guid ApBillId { get; set; }
    public short LineNo { get; set; }
    public string Description { get; set; } = string.Empty;
    public long NetAmountMinor { get; set; }       // pre-tax net for this line
    public string ExpenseAccount { get; set; } = "5040";
    public string CurrencyCode { get; set; } = "GHS";
}

public enum ApBillStatus
{
    Captured = 0,             // entered, awaiting approval
    Approved = 1,             // posted to ledger as DR expense / CR AP control
    PartiallyPaid = 2,
    Paid = 3,
    Void = 4
}

/// <summary>One payment of one bill via one rail. Multiple payments per bill possible.</summary>
public class ApPayment
{
    public Guid ApPaymentId { get; set; } = Guid.NewGuid();
    public Guid ApBillId { get; set; }
    public DateOnly PaymentDate { get; set; }
    public long AmountMinor { get; set; }
    public string CurrencyCode { get; set; } = "GHS";
    public string PaymentRail { get; set; } = "bank";  // "cash", "bank", "momo:hubtel"
    public string CashAccount { get; set; } = "1030";  // ledger code credited
    public string? RailReference { get; set; }
    public Guid PaymentRunId { get; set; }             // groups payments paid together
    public Guid RecordedByUserId { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? LedgerEventId { get; set; }
    public long TenantId { get; set; } = 1;
}

/// <summary>One WHT certificate — the receipt the supplier receives that they then claim against their own GRA tax position.</summary>
public class WhtCertificate
{
    public Guid WhtCertificateId { get; set; } = Guid.NewGuid();
    public string CertificateNo { get; set; } = string.Empty;   // e.g. "WHT-2026-04-00007"
    public Guid VendorId { get; set; }
    public Guid? ApBillId { get; set; }
    public Guid? ApPaymentId { get; set; }
    public DateOnly IssueDate { get; set; }
    public long GrossPaidMinor { get; set; }
    public long WhtDeductedMinor { get; set; }
    public decimal WhtRate { get; set; }
    public WhtTransactionType TransactionType { get; set; }
    public string CurrencyCode { get; set; } = "GHS";
    public DateTimeOffset CreatedAt { get; set; }
    public long TenantId { get; set; } = 1;
}
