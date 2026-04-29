using NickERP.Platform.Tenancy.Entities;

namespace NickERP.NickFinance.Core.Entities;

/// <summary>
/// Soft period lock — see G2 §1.7. One row per (tenant, year-month);
/// when <see cref="ClosedAt"/> is non-null the period is closed.
///
/// <para>
/// Posting a ledger event whose <see cref="LedgerEvent.PostedAt"/> falls
/// inside a closed period throws unless the actor holds the
/// <c>petty_cash.reopen_period</c> role. Late posts emit a
/// <c>nickfinance.petty_cash.late_post</c> audit event for SOX-style
/// review.
/// </para>
///
/// <para>
/// Composite primary key: (TenantId, PeriodYearMonth). PeriodYearMonth
/// is stored as text in <c>YYYY-MM</c> format. Out-of-the-box months
/// may not have a row at all — absence == open. The first close creates
/// the row; reopen sets <see cref="ClosedAt"/> back to null.
/// </para>
/// </summary>
public sealed class PettyCashPeriod : ITenantOwned
{
    /// <summary>Composite key part 1.</summary>
    public long TenantId { get; set; }

    /// <summary>Composite key part 2. Format: <c>YYYY-MM</c> (e.g. <c>"2026-04"</c>).</summary>
    public string PeriodYearMonth { get; set; } = string.Empty;

    /// <summary>When the period was closed. NULL = open.</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Who closed the period. NULL when open.</summary>
    public Guid? ClosedByUserId { get; set; }

    /// <summary><c>true</c> when <see cref="ClosedAt"/> is non-null.</summary>
    public bool IsClosed => ClosedAt.HasValue;
}
