using System.Diagnostics;
using NickFinance.Ledger;
using NickFinance.PettyCash;
using Npgsql;

namespace NickFinance.WebApp.Services;

/// <summary>
/// Translates an exception into a user-language string. The raw exception
/// is meant for the log; the operator at the port wants to know what to
/// do next, not "Voucher ledger event posting failed because the deferred
/// balance trigger fired" or a SQL constraint name. Falls back to a
/// generic "Something went wrong" with the OpenTelemetry trace id so a
/// support ticket can find the request.
/// </summary>
public interface IExceptionTranslator
{
    /// <summary>Friendly UI message. Caller still logs the raw exception.</summary>
    string Friendly(Exception ex);
}

/// <summary>
/// Default implementation. Recognises the domain exception types from the
/// kernel + modules; recognises the most common Postgres SqlState codes
/// (uniqueness, FK, check); falls through to a trace-id-stamped generic.
/// </summary>
public sealed class ExceptionTranslator : IExceptionTranslator
{
    public string Friendly(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // Domain exceptions — most specific message wins.
        switch (ex)
        {
            case VoucherTotalMismatchException totalEx:
                return $"Line items don't sum to the voucher total (expected {totalEx.ExpectedMinor / 100m:N2}, got {totalEx.LineSumMinor / 100m:N2}).";
            case FloatNotAvailableException:
                return "That float is closed or doesn't exist. Pick a different float, or ask a finance lead to provision one.";
            case InvalidVoucherTransitionException trEx:
                return $"This voucher can't be {trEx.Operation} from its current state ({trEx.From}).";
            case SeparationOfDutiesException sodEx:
                return sodEx.Message; // already user-facing wording from the domain
            case PettyCashException petty:
                return petty.Message;
            case UnbalancedJournalException unbalanced:
                return $"Journal entry is unbalanced: debits {unbalanced.DebitsMinor / 100m:N2} ≠ credits {unbalanced.CreditsMinor / 100m:N2}.";
            case ClosedPeriodException closed:
                return $"That accounting period is {closed.Status}; postings to it are blocked. Open the period first or pick a different date.";
            case MalformedLineException malformed:
                return $"Journal line is malformed: {malformed.Message}";
            case InvalidReversalException invRev:
                return $"Reversal target is invalid: {invRev.Message}";
            case LedgerException ledger:
                return ledger.Message;
        }

        // Postgres — unwrap the inner if EF wrapped a Npgsql exception.
        var pg = FindPostgresException(ex);
        if (pg is not null)
        {
            return TranslatePostgres(pg);
        }

        // Last-resort generic with the current trace id so support can dig.
        var trace = Activity.Current?.Id;
        return string.IsNullOrEmpty(trace)
            ? "Something went wrong. Please try again or contact finance."
            : $"Something went wrong. Trace id: {trace}";
    }

    private static PostgresException? FindPostgresException(Exception ex)
    {
        Exception? cur = ex;
        while (cur is not null)
        {
            if (cur is PostgresException pg) return pg;
            cur = cur.InnerException;
        }
        return null;
    }

    private static string TranslatePostgres(PostgresException pg)
    {
        // Common SqlStates surfaced from EF -> Npgsql.
        // 23505 unique_violation
        // 23503 foreign_key_violation
        // 23502 not_null_violation
        // 23514 check_violation
        // 40P01 deadlock_detected
        // 40001 serialization_failure
        return pg.SqlState switch
        {
            "23505" => "This record already exists (a uniqueness rule was violated).",
            "23503" => "Cannot complete the action — a related record is missing or in use.",
            "23502" => "A required field is missing.",
            "23514" => "The data fails a database check (a value is out of range or in the wrong shape).",
            "40P01" or "40001" => "The system was busy. Please try again in a moment.",
            _ => $"Database refused the change ({pg.SqlState}). Trace id: {Activity.Current?.Id ?? "(none)"}"
        };
    }
}
