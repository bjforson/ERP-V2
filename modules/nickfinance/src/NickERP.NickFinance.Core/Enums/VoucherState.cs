namespace NickERP.NickFinance.Core.Enums;

/// <summary>
/// Voucher state machine — see G2 §1.4.
///
/// <para>
/// Linear happy path: <c>Request → Approve → Disburse → Reconcile</c>.
/// Terminal exits: <c>Rejected</c> from <c>Request</c>; <c>Cancelled</c>
/// from <c>Request</c> or <c>Approve</c> only — once disbursed, only a
/// reconcile-time Adjust ledger event can correct an over- or under-spend.
/// </para>
///
/// <para>
/// Stored as text in <c>petty_cash_vouchers.state</c> (lowercase). The
/// EF mapping uses an int conversion in the snapshot but the migration
/// generates a check-constraint-free text column —
/// <c>VoucherTransitions.IsAllowed</c> is the single source of truth for
/// valid transitions and validates them up-front.
/// </para>
/// </summary>
public enum VoucherState
{
    /// <summary>Initial state — requester has filled out the voucher; awaiting approver.</summary>
    Request = 0,

    /// <summary>Approver has signed off; awaiting custodian to hand out the cash.</summary>
    Approve = 1,

    /// <summary>Custodian has handed out the cash. A debit ledger event has been posted.</summary>
    Disburse = 2,

    /// <summary>Receipts attached, difference (if any) resolved by an Adjust event. Terminal.</summary>
    Reconcile = 3,

    /// <summary>Approver rejected the request. Terminal.</summary>
    Rejected = 4,

    /// <summary>Requester or approver cancelled before disburse. Terminal.</summary>
    Cancelled = 5
}
