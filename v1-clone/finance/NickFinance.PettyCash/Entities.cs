using NickFinance.Ledger;

namespace NickFinance.PettyCash;

// ---------------------------------------------------------------------------
// Float — one cash/MoMo float per custodian per site. The custodian holds
// the physical money; vouchers draw from this float; periodic top-ups
// replenish it.
// ---------------------------------------------------------------------------

/// <summary>
/// One imprest float held by a custodian at a site. The float carries the
/// running balance in minor units; ledger projections recompute from
/// <see cref="LedgerEvent"/> rows when an authoritative number is needed.
/// </summary>
public class Float
{
    public Guid FloatId { get; set; } = Guid.NewGuid();

    /// <summary>The site the float is attached to (Tema, Takoradi, etc.).</summary>
    public Guid SiteId { get; set; }

    /// <summary>Canonical user id of the custodian who holds this float.</summary>
    public Guid CustodianUserId { get; set; }

    /// <summary>ISO 4217 currency code. <c>GHS</c> in v1.</summary>
    public string CurrencyCode { get; set; } = "GHS";

    /// <summary>Initial / agreed float amount in minor units. Top-ups replenish back to this value.</summary>
    public long FloatAmountMinor { get; set; }

    /// <summary>
    /// Convenience accessor for the float as a <see cref="Money"/>. Set
    /// updates both <see cref="FloatAmountMinor"/> and <see cref="CurrencyCode"/>.
    /// </summary>
    public Money FloatAmount
    {
        get => new(FloatAmountMinor, CurrencyCode);
        set
        {
            FloatAmountMinor = value.Minor;
            CurrencyCode = value.CurrencyCode;
        }
    }

    /// <summary>0..100 — the reader can compare current balance vs this to flag for replenishment.</summary>
    public short ReplenishThresholdPct { get; set; } = 25;

    /// <summary><c>true</c> while the custodian is active. Closing a float marks it inactive.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The user (admin / finance) that provisioned this float.</summary>
    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }

    /// <summary>Multi-tenant isolation key.</summary>
    public long TenantId { get; set; } = 1;
}

// ---------------------------------------------------------------------------
// Voucher — a single petty-cash request from Draft to Disbursed
// ---------------------------------------------------------------------------

/// <summary>
/// One petty-cash voucher. The state machine — see <see cref="VoucherStatus"/>
/// — drives which operations are valid; <see cref="PettyCashService"/>
/// enforces transitions atomically.
/// </summary>
public class Voucher
{
    public Guid VoucherId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-friendly identifier: <c>PC-{site-prefix}-{year}-{seq}</c>, e.g.
    /// <c>PC-TEMA-2026-00421</c>. The service generates this on submit using
    /// a per-site monotonic counter.
    /// </summary>
    public string VoucherNo { get; set; } = string.Empty;

    /// <summary>FK to <see cref="Float"/> — which custodian's float this draws from.</summary>
    public Guid FloatId { get; set; }

    /// <summary>The user who raised the voucher (and is its initial requester).</summary>
    public Guid RequesterUserId { get; set; }

    /// <summary>Spend category — drives the GL account hit on disbursement.</summary>
    public VoucherCategory Category { get; set; }

    /// <summary>Free-text business justification for the spend.</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Requested amount in minor units (pesewa for GHS).</summary>
    public long AmountRequestedMinor { get; set; }

    /// <summary>Approved amount in minor units. May differ from requested when an approver part-approves; <see langword="null"/> until approval.</summary>
    public long? AmountApprovedMinor { get; set; }

    public string CurrencyCode { get; set; } = "GHS";

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    /// <summary>Optional payee — the third party receiving the cash, if not the requester themselves.</summary>
    public string? PayeeName { get; set; }

    /// <summary>Mobile money number of the payee, when the disbursement channel is MoMo.</summary>
    public string? PayeeMomoNumber { get; set; }

    /// <summary>MoMo network label — <c>MTN</c>, <c>VODA</c>, <c>ATM</c>.</summary>
    public string? PayeeMomoNetwork { get; set; }

    /// <summary>Optional cost-centre / project code recorded on the journal line as a dimension.</summary>
    public string? ProjectCode { get; set; }

    /// <summary>The rail used to disburse — <c>cash</c>, <c>momo:hubtel</c>, etc. Set on the Disbursed transition.</summary>
    public string? DisbursementChannel { get; set; }

    /// <summary>The rail's reference (transaction id, MoMo reference). Set on Disbursed.</summary>
    public string? DisbursementReference { get; set; }

    /// <summary>
    /// How taxes apply to this voucher. <see cref="TaxTreatment.None"/> (the
    /// default) means lines post at face value. <see cref="TaxTreatment.GhanaInclusive"/>
    /// means the gross-line amounts include NHIL/GETFund/COVID/VAT, and the
    /// disbursement journal will split them into the four levy/VAT payable
    /// accounts so the GL stays compliant.
    /// </summary>
    public TaxTreatment TaxTreatment { get; set; } = TaxTreatment.None;

    /// <summary>Withholding tax classification for the supplier; <see cref="WhtTreatment.None"/> = no WHT deducted.</summary>
    public WhtTreatment WhtTreatment { get; set; } = WhtTreatment.None;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? DisbursedAt { get; set; }

    /// <summary>The approver / rejecter — set on the Approved or Rejected transition.</summary>
    public Guid? DecidedByUserId { get; set; }

    /// <summary>Approver's comment on Approved/Rejected; reason when rejected.</summary>
    public string? DecisionComment { get; set; }

    /// <summary>
    /// The custodian who actually paid the cash — set on Disbursed. Must
    /// differ from <see cref="DecidedByUserId"/> (separation of duties).
    /// </summary>
    public Guid? DisbursedByUserId { get; set; }

    /// <summary>
    /// The id of the <see cref="LedgerEvent"/> posted on disbursement. Lets
    /// auditors trace voucher -> journal in one hop.
    /// </summary>
    public Guid? LedgerEventId { get; set; }

    public long TenantId { get; set; } = 1;

    public List<VoucherLineItem> Lines { get; set; } = new();
}

/// <summary>
/// One line item on a voucher. v1 tracks gross amount + GL account; the
/// tax engine (Phase 6.5) will plug in via additional fields and never
/// change the gross.
/// </summary>
public class VoucherLineItem
{
    public Guid VoucherLineId { get; set; } = Guid.NewGuid();
    public Guid VoucherId { get; set; }

    /// <summary>1-based ordinal within the voucher.</summary>
    public short LineNo { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Gross amount in minor units (i.e. inclusive of any taxes; tax engine will split later).</summary>
    public long GrossAmountMinor { get; set; }

    /// <summary>The chart-of-accounts code this line books against. Typically defaulted from the voucher category.</summary>
    public string GlAccount { get; set; } = string.Empty;

    public string CurrencyCode { get; set; } = "GHS";
}

// ---------------------------------------------------------------------------
// State machine
// ---------------------------------------------------------------------------

/// <summary>Voucher lifecycle. See <c>PETTY_CASH_MVP.md</c> for transitions.</summary>
public enum VoucherStatus
{
    /// <summary>Created but not yet submitted; only the requester can edit.</summary>
    Draft = 0,

    /// <summary>Awaiting approver decision.</summary>
    Submitted = 1,

    /// <summary>Approved — ready for the custodian to disburse.</summary>
    Approved = 2,

    /// <summary>Rejected — the requester may clone &amp; resubmit but this row is terminal.</summary>
    Rejected = 3,

    /// <summary>Cash paid out and journal posted to the Ledger.</summary>
    Disbursed = 4
}

/// <summary>How a voucher's gross amount should be decomposed for the journal.</summary>
public enum TaxTreatment
{
    /// <summary>No tax handling — lines post at face value (default; covers the bulk of petty cash).</summary>
    None = 0,

    /// <summary>
    /// The line gross amounts INCLUDE Ghana indirect taxes (NHIL+GETFund+COVID+VAT).
    /// On disbursement we back-solve the net per <see cref="TaxEngine.TaxCalculator.FromGross"/>
    /// and post one line each to the levy + VAT payable / receivable accounts.
    /// </summary>
    GhanaInclusive = 1
}

/// <summary>How withholding tax is treated when paying a supplier from petty cash.</summary>
public enum WhtTreatment
{
    /// <summary>No WHT deducted (default — most petty cash payees aren't subject).</summary>
    None = 0,

    /// <summary>3% — supply of goods to a VAT-registered vendor.</summary>
    GoodsVatRegistered = 1,

    /// <summary>7% — supply of goods to a non-VAT-registered vendor.</summary>
    GoodsNonVatRegistered = 2,

    /// <summary>5% — supply of works (construction, fabrication).</summary>
    Works = 3,

    /// <summary>7.5% — supply of services / management / technical / consulting.</summary>
    Services = 4,

    /// <summary>8% — rent.</summary>
    Rent = 5,

    /// <summary>10% — commission to agents.</summary>
    Commission = 6
}

/// <summary>The five allowed spend categories in MVP-zero. Each maps to a default GL account.</summary>
public enum VoucherCategory
{
    Transport = 1,
    Fuel = 2,
    OfficeSupplies = 3,
    StaffWelfare = 4,
    Emergency = 5
}

/// <summary>Maps <see cref="VoucherCategory"/> to its default chart-of-accounts code.</summary>
public static class VoucherCategoryExtensions
{
    /// <summary>Default GL expense code for the category — used unless a line item overrides.</summary>
    public static string DefaultGlAccount(this VoucherCategory category) => category switch
    {
        VoucherCategory.Transport => "6300",        // Travel
        VoucherCategory.Fuel => "6310",             // Vehicle running
        VoucherCategory.OfficeSupplies => "6410",   // Office supplies
        VoucherCategory.StaffWelfare => "6900",     // Other operating expense (welfare lives here in MVP)
        VoucherCategory.Emergency => "6400",        // Petty cash expense — general
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown VoucherCategory.")
    };
}
