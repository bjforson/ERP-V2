namespace NickERP.NickFinance.Core.Enums;

/// <summary>
/// Direction of a cash movement — see G2 §1.3.
///
/// <para>
/// Money values are non-negative; direction lives here so a reader of a
/// <see cref="Entities.LedgerEvent"/> never has to puzzle over a signed
/// amount. Reversals are paired rows (original + new with opposite
/// direction + <c>corrects_event_id</c>) — see §1.6.
/// </para>
/// </summary>
public enum LedgerDirection
{
    /// <summary>Cash leaving the box.</summary>
    Debit = 0,

    /// <summary>Cash entering the box.</summary>
    Credit = 1,

    /// <summary>Reconciliation adjustment — sign carried by paired Debit/Credit rows when needed.</summary>
    Adjust = 2
}
