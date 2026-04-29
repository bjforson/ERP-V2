using NickERP.Platform.Tenancy.Entities;

namespace NickERP.NickFinance.Core.Entities;

/// <summary>
/// One physical / logical petty-cash box. Tenant-owned; a tenant may run
/// multiple boxes (one per currency, one per branch, one per project).
/// See G2 §1.2 and §1.5.
///
/// <para>
/// Each box has a fixed <see cref="CurrencyCode"/> — vouchers raised
/// against the box inherit this currency; cross-currency activity goes
/// through a different box. A box has both a <see cref="CustodianUserId"/>
/// (the person who actually hands out the cash) and an
/// <see cref="ApproverUserId"/> (the person who signs off on requests
/// before disbursement). The two MUST be different — separation-of-duties
/// is enforced by a DB CHECK constraint, not just by application code.
/// </para>
/// </summary>
public sealed class PettyCashBox : ITenantOwned
{
    /// <summary>Stable primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short, unique-per-tenant code (kebab-case or shouty-snake; e.g. <c>"hq-ops"</c> / <c>"PORT-NSAWAM"</c>).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Display name shown in the UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>ISO 4217 currency code. Vouchers raised on this box inherit this code.</summary>
    public string CurrencyCode { get; set; } = "GHS";

    /// <summary>The user who hands out the cash. Required at creation; required for the Disburse transition.</summary>
    public Guid CustodianUserId { get; set; }

    /// <summary>The user who signs off on voucher requests. Required at creation; required for the Approve transition. CHECK !=  CustodianUserId.</summary>
    public Guid ApproverUserId { get; set; }

    /// <summary>Opening balance amount — the amount the box was funded with at creation. Currency must match <see cref="CurrencyCode"/>.</summary>
    public decimal OpeningBalanceAmount { get; set; }

    /// <summary>Currency of the opening balance. CHECK = <see cref="CurrencyCode"/>.</summary>
    public string OpeningBalanceCurrency { get; set; } = "GHS";

    /// <summary>When the box was opened.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the box was archived. NULL = active. Soft-delete only — vouchers and ledger events keep referring to archived boxes for audit.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <inheritdoc />
    public long TenantId { get; set; }

    /// <summary>Convenience: the opening balance as a <see cref="Money"/> value object.</summary>
    public Money OpeningBalance => new(OpeningBalanceAmount, OpeningBalanceCurrency);
}
