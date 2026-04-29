namespace NickERP.NickFinance.Core.Roles;

/// <summary>
/// Stable role / claim names emitted by the NickFinance module. Today
/// these are checked against ASP.NET Identity claims directly; a
/// future sprint will move them into the platform's role-management
/// story. The constants live here so renames are caught at compile
/// time and the audit register can reference them by symbol.
/// </summary>
public static class PettyCashRoles
{
    /// <summary>
    /// Allows posting (or back-dating) a ledger event into a closed
    /// period. Without this role, posting into a closed period throws
    /// <c>PeriodLockedException</c>; with it, the post succeeds and
    /// emits a <c>nickfinance.petty_cash.late_post</c> audit event.
    /// </summary>
    public const string ReopenPeriod = "petty_cash.reopen_period";

    /// <summary>
    /// Allows publishing rows into the suite-wide <c>fx_rate</c> table.
    /// Backed by a <see cref="NickERP.Platform.Tenancy.ITenantContext.SetSystemContext"/>
    /// call; the caller is registered in
    /// <c>docs/system-context-audit-register.md</c>.
    /// </summary>
    public const string PublishFx = "petty_cash.publish_fx";

    /// <summary>
    /// Allows closing or reopening a period (without posting into it).
    /// Today the same role as <see cref="ReopenPeriod"/>; kept distinct
    /// so a future sprint can decouple them.
    /// </summary>
    public const string ManagePeriods = "petty_cash.manage_periods";
}
