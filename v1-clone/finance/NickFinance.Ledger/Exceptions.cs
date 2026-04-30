namespace NickFinance.Ledger;

public class LedgerException : Exception
{
    public LedgerException(string message) : base(message) { }
    public LedgerException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>SUM(debits) != SUM(credits) for a posted event.</summary>
public sealed class UnbalancedJournalException : LedgerException
{
    public long DebitsMinor { get; }
    public long CreditsMinor { get; }
    public UnbalancedJournalException(long debits, long credits)
        : base($"Journal is unbalanced: debits={debits} credits={credits} diff={debits - credits} (minor units).")
    {
        DebitsMinor = debits;
        CreditsMinor = credits;
    }
}

/// <summary>A line has both debit and credit non-zero, or both zero, or a mixed currency across lines.</summary>
public sealed class MalformedLineException : LedgerException
{
    public MalformedLineException(string message) : base(message) { }
}

/// <summary>Period is SoftClosed or HardClosed and the caller isn't authorised to adjust.</summary>
public sealed class ClosedPeriodException : LedgerException
{
    public Guid PeriodId { get; }
    public PeriodStatus Status { get; }
    public ClosedPeriodException(Guid periodId, PeriodStatus status)
        : base($"Period {periodId} is {status}; posting rejected.")
    {
        PeriodId = periodId;
        Status = status;
    }
}

/// <summary>Reversal references an event that doesn't exist or is itself a reversal.</summary>
public sealed class InvalidReversalException : LedgerException
{
    public InvalidReversalException(string message) : base(message) { }
}
