using NickERP.NickFinance.Core.Enums;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.NickFinance.Core.Entities;

/// <summary>
/// One row in the append-only petty-cash ledger. Every cash movement
/// becomes one of these. Reversals = a new row with opposite
/// <see cref="Direction"/> and <see cref="CorrectsEventId"/> pointing at
/// the original row. See G2 §1.6 + §1.8.
///
/// <para>
/// Single-entry today; double-entry GL projection (paired Debit/Credit
/// rows against a Chart of Accounts) is a future projection table, NOT
/// a mutation of these rows. We can build the GL view from this stream
/// at any time without losing fidelity.
/// </para>
///
/// <para>
/// Every event carries BOTH the native amount (in box currency) AND the
/// base amount (in tenant base currency) snapshot at <see cref="PostedAt"/>.
/// Reports use <see cref="AmountBase"/> for cross-currency aggregation;
/// historical reports stay stable when FX rates move.
/// </para>
/// </summary>
public sealed class LedgerEvent : ITenantOwned
{
    /// <summary>Stable primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Owning box.</summary>
    public Guid BoxId { get; set; }

    /// <summary>The voucher this event corresponds to. NULL for replenishment events (custodian top-ups, no voucher).</summary>
    public Guid? VoucherId { get; set; }

    /// <summary>What kind of cash movement this is.</summary>
    public LedgerEventType EventType { get; set; }

    /// <summary>Direction of cash flow (debit / credit / adjust).</summary>
    public LedgerDirection Direction { get; set; }

    /// <summary>The amount in the box's native currency. Always positive — direction lives on <see cref="Direction"/>.</summary>
    public decimal AmountNative { get; set; }

    /// <summary>ISO 4217 native currency code (= <see cref="PettyCashBox.CurrencyCode"/>).</summary>
    public string CurrencyNative { get; set; } = "GHS";

    /// <summary>The same amount converted to the tenant base currency using the FX rate effective at <see cref="PostedAt"/>'s date.</summary>
    public decimal AmountBase { get; set; }

    /// <summary>Tenant base currency code (= <c>tenant.base_currency_code</c>).</summary>
    public string CurrencyBase { get; set; } = "GHS";

    /// <summary>The FX rate (native → base) actually applied. <c>1.0</c> when native == base.</summary>
    public decimal FxRate { get; set; }

    /// <summary>The date of the FX rate row that was looked up.</summary>
    public DateTime FxRateDate { get; set; }

    /// <summary>When the event was posted (the canonical "when did this cash move" timestamp).</summary>
    public DateTimeOffset PostedAt { get; set; }

    /// <summary>Who posted the event (custodian / approver / system).</summary>
    public Guid PostedByUserId { get; set; }

    /// <summary>Reversal pointer: when set, this row reverses the event with this id (opposite direction).</summary>
    public Guid? CorrectsEventId { get; set; }

    /// <inheritdoc />
    public long TenantId { get; set; }

    /// <summary>Convenience: amount as a Money value (native currency).</summary>
    public Money AmountNativeMoney => new(AmountNative, CurrencyNative);

    /// <summary>Convenience: amount as a Money value (base currency).</summary>
    public Money AmountBaseMoney => new(AmountBase, CurrencyBase);
}
