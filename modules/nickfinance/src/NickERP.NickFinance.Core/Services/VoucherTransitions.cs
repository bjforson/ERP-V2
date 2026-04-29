using NickERP.NickFinance.Core.Enums;

namespace NickERP.NickFinance.Core.Services;

/// <summary>
/// Pure-function validator for the voucher state machine — see G2 §1.4.
/// Lives in Core so unit tests cover the transition matrix without
/// touching DB / DI. Workflow service wraps these checks and adds
/// actor-authorization on top.
///
/// <para>
/// Allowed transitions:
/// </para>
/// <list type="bullet">
///   <item><description>Request → Approve (approver acts)</description></item>
///   <item><description>Request → Rejected (approver acts)</description></item>
///   <item><description>Request → Cancelled (requester acts)</description></item>
///   <item><description>Approve → Disburse (custodian acts; emits Debit ledger event)</description></item>
///   <item><description>Approve → Cancelled (requester or approver acts)</description></item>
///   <item><description>Disburse → Reconcile (terminal, may emit Adjust ledger event)</description></item>
/// </list>
/// <para>
/// Anything else is forbidden; in particular, no Cancel after Disburse
/// (over-spend goes through reconcile-Adjust, see §1.4).
/// </para>
/// </summary>
public static class VoucherTransitions
{
    /// <summary>
    /// Returns true if a voucher in <paramref name="from"/> may move to
    /// <paramref name="to"/>. Pure function; no side effects.
    /// </summary>
    public static bool IsAllowed(VoucherState from, VoucherState to) =>
        (from, to) switch
        {
            (VoucherState.Request, VoucherState.Approve) => true,
            (VoucherState.Request, VoucherState.Rejected) => true,
            (VoucherState.Request, VoucherState.Cancelled) => true,
            (VoucherState.Approve, VoucherState.Disburse) => true,
            (VoucherState.Approve, VoucherState.Cancelled) => true,
            (VoucherState.Disburse, VoucherState.Reconcile) => true,
            _ => false
        };

    /// <summary>
    /// Throw <see cref="InvalidVoucherTransitionException"/> if the
    /// transition is not allowed. Use at the head of every workflow
    /// transition method.
    /// </summary>
    public static void EnsureAllowed(VoucherState from, VoucherState to)
    {
        if (!IsAllowed(from, to))
        {
            throw new InvalidVoucherTransitionException(from, to);
        }
    }
}

/// <summary>
/// Thrown when a voucher transition is attempted that is not in the
/// state-machine matrix. Distinct exception type so callers (UI,
/// endpoint handler) can surface a clear 409 / human-friendly message
/// without sniffing message strings.
/// </summary>
public sealed class InvalidVoucherTransitionException : InvalidOperationException
{
    /// <summary>State the voucher was actually in.</summary>
    public VoucherState From { get; }

    /// <summary>State the caller tried to move it to.</summary>
    public VoucherState To { get; }

    public InvalidVoucherTransitionException(VoucherState from, VoucherState to)
        : base(BuildMessage(from, to))
    {
        From = from;
        To = to;
    }

    private static string BuildMessage(VoucherState from, VoucherState to)
        => $"Voucher transition {from} → {to} is not allowed. "
           + "Allowed paths: Request→Approve|Rejected|Cancelled, "
           + "Approve→Disburse|Cancelled, Disburse→Reconcile.";
}

/// <summary>
/// Thrown when an actor attempts a voucher transition they are not
/// authorized for (e.g. a requester trying to Approve their own
/// voucher, or a custodian trying to Approve when only the approver
/// can). Distinct from <see cref="InvalidVoucherTransitionException"/>
/// so callers can map to 403 vs 409.
/// </summary>
public sealed class UnauthorizedVoucherActorException : InvalidOperationException
{
    /// <summary>The user who tried the action.</summary>
    public Guid ActorUserId { get; }

    /// <summary>The action they tried.</summary>
    public string Action { get; }

    /// <summary>Who SHOULD have performed the action.</summary>
    public Guid ExpectedActorUserId { get; }

    public UnauthorizedVoucherActorException(Guid actorUserId, string action, Guid expectedActorUserId)
        : base($"User {actorUserId} cannot perform '{action}' — that's the responsibility of {expectedActorUserId}.")
    {
        ActorUserId = actorUserId;
        Action = action;
        ExpectedActorUserId = expectedActorUserId;
    }
}

/// <summary>
/// Thrown when a ledger event would post into a closed period and the
/// caller does not hold <see cref="Roles.PettyCashRoles.ReopenPeriod"/>.
/// </summary>
public sealed class PeriodLockedException : InvalidOperationException
{
    /// <summary>The closed period (e.g. <c>"2026-04"</c>).</summary>
    public string PeriodYearMonth { get; }

    public PeriodLockedException(string periodYearMonth)
        : base($"Period {periodYearMonth} is closed; posting requires the petty_cash.reopen_period role.")
    {
        PeriodYearMonth = periodYearMonth;
    }
}

/// <summary>
/// Thrown when a ledger write needs an FX rate but no row has been
/// published for the (from, to, on-or-before-date) tuple. Callers
/// should map this to a user-facing message asking finance to publish
/// today's rates (see §1.10).
/// </summary>
public sealed class FxRateNotPublishedException : InvalidOperationException
{
    public string FromCurrency { get; }
    public string ToCurrency { get; }
    public DateTime EffectiveDate { get; }

    public FxRateNotPublishedException(string fromCurrency, string toCurrency, DateTime effectiveDate)
        : base($"FX rate not yet published for {fromCurrency}→{toCurrency} on {effectiveDate:yyyy-MM-dd}; "
               + "ask finance to publish today's rates.")
    {
        FromCurrency = fromCurrency;
        ToCurrency = toCurrency;
        EffectiveDate = effectiveDate;
    }
}
