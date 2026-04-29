using NickERP.NickFinance.Core.Enums;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.NickFinance.Core.Entities;

/// <summary>
/// One petty-cash voucher — the unit of work in the petty-cash workflow.
/// State transitions are owned by <c>VoucherWorkflowService</c>; entity
/// fields are mostly written by the workflow, not by application code
/// directly.
///
/// <para>
/// State machine: <c>Request → Approve → Disburse → Reconcile</c>, plus
/// terminal exits <c>Rejected</c> and <c>Cancelled</c>. See G2 §1.4.
/// Cash moves on <see cref="VoucherState.Disburse"/> only — that's the
/// transition that emits a Debit ledger event.
/// </para>
/// </summary>
public sealed class Voucher : ITenantOwned
{
    /// <summary>Stable primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning box. Vouchers don't move between boxes.</summary>
    public Guid BoxId { get; set; }
    public PettyCashBox? Box { get; set; }

    /// <summary>Per-box monotonic sequence number (1, 2, 3, ...). Used for human reference (<c>HQ-OPS-0042</c>).</summary>
    public long SequenceNumber { get; set; }

    /// <summary>Current state. Mutated by <c>VoucherWorkflowService</c>; do not write directly.</summary>
    public VoucherState State { get; set; } = VoucherState.Request;

    /// <summary>Free-text purpose description (e.g. "Office supplies — printer toner").</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The amount the requester is asking for, in the box's currency.</summary>
    public decimal RequestedAmount { get; set; }

    /// <summary>Currency of <see cref="RequestedAmount"/>. Must equal <see cref="PettyCashBox.CurrencyCode"/> on the parent box.</summary>
    public string RequestedCurrency { get; set; } = "GHS";

    /// <summary>The same amount converted to the tenant's base currency at request time. Snapshot — does NOT update if FX moves.</summary>
    public decimal RequestedAmountBase { get; set; }

    /// <summary>Tenant base currency at the time of request.</summary>
    public string RequestedCurrencyBase { get; set; } = "GHS";

    /// <summary>Who raised the voucher. Required.</summary>
    public Guid RequestedByUserId { get; set; }

    /// <summary>When the voucher was raised. UTC.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>Who approved (or rejected) the voucher. Set on the Approve / Reject transition.</summary>
    public Guid? ApproverUserId { get; set; }

    /// <summary>When the voucher was approved. NULL until Approve transition fires.</summary>
    public DateTimeOffset? ApprovedAt { get; set; }

    /// <summary>The amount actually disbursed (may differ from <see cref="RequestedAmount"/> on partial disburse — out of v0 scope; today equals requested). Native currency.</summary>
    public decimal? DisbursedAmount { get; set; }

    /// <summary>Currency of <see cref="DisbursedAmount"/>; equals <see cref="RequestedCurrency"/>.</summary>
    public string? DisbursedCurrency { get; set; }

    /// <summary>The disbursed amount converted to base at disburse time.</summary>
    public decimal? DisbursedAmountBase { get; set; }

    /// <summary>Tenant base currency at disburse time.</summary>
    public string? DisbursedCurrencyBase { get; set; }

    /// <summary>When the cash was handed out. NULL until Disburse transition.</summary>
    public DateTimeOffset? DisbursedAt { get; set; }

    /// <summary>When receipts were attached and the voucher reconciled.</summary>
    public DateTimeOffset? ReconciledAt { get; set; }

    /// <summary>Reason given by the approver on rejection. Free-text.</summary>
    public string? RejectedReason { get; set; }

    /// <summary>When the voucher was cancelled (by requester / approver pre-disburse).</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <inheritdoc />
    public long TenantId { get; set; }

    /// <summary>Convenience: the requested amount as a Money value object.</summary>
    public Money RequestedMoney => new(RequestedAmount, RequestedCurrency);

    /// <summary>Convenience: the disbursed amount as a Money value object, or null if not yet disbursed.</summary>
    public Money? DisbursedMoney =>
        DisbursedAmount.HasValue && !string.IsNullOrEmpty(DisbursedCurrency)
            ? new Money(DisbursedAmount.Value, DisbursedCurrency)
            : null;
}
