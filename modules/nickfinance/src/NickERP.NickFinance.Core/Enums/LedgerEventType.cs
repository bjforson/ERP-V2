namespace NickERP.NickFinance.Core.Enums;

/// <summary>
/// What kind of cash movement a ledger event represents. See G2 §1.6.
/// Stored as lowercase text in <c>petty_cash_ledger_events.event_type</c>.
/// </summary>
public enum LedgerEventType
{
    /// <summary>Cash leaving the box for an approved voucher (Disburse transition).</summary>
    Disburse = 0,

    /// <summary>Refund into the box. Reserved — voucher reconcile uses Adjust, not Refund.</summary>
    Refund = 1,

    /// <summary>Cash put into the box (custodian top-up). Voucher_id is NULL.</summary>
    Replenish = 2,

    /// <summary>Reconciliation difference (over- or under-spend). Voucher_id is set.</summary>
    Adjust = 3
}
