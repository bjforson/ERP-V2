namespace NickFinance.PettyCash;

/// <summary>Base type for everything <see cref="PettyCashService"/> can raise.</summary>
public class PettyCashException : Exception
{
    public PettyCashException(string message) : base(message) { }
    public PettyCashException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a state transition isn't allowed (e.g. approving a Disbursed voucher).</summary>
public sealed class InvalidVoucherTransitionException : PettyCashException
{
    public VoucherStatus From { get; }
    public string Operation { get; }

    public InvalidVoucherTransitionException(VoucherStatus from, string operation)
        : base($"Cannot {operation} a voucher in state {from}.")
    {
        From = from;
        Operation = operation;
    }
}

/// <summary>Thrown when separation-of-duties is violated (e.g. approver disbursing the same voucher).</summary>
public sealed class SeparationOfDutiesException : PettyCashException
{
    public SeparationOfDutiesException(string message) : base(message) { }
}

/// <summary>Thrown when the lines on a voucher don't sum to the requested/approved amount.</summary>
public sealed class VoucherTotalMismatchException : PettyCashException
{
    public long ExpectedMinor { get; }
    public long LineSumMinor { get; }

    public VoucherTotalMismatchException(long expected, long sum)
        : base($"Voucher line items sum to {sum}, but the voucher amount is {expected}.")
    {
        ExpectedMinor = expected;
        LineSumMinor = sum;
    }
}

/// <summary>Thrown when a voucher references a float that doesn't exist or is closed.</summary>
public sealed class FloatNotAvailableException : PettyCashException
{
    public Guid FloatId { get; }
    public FloatNotAvailableException(Guid floatId, string reason)
        : base($"Float {floatId} is not available: {reason}.")
    {
        FloatId = floatId;
    }
}
