namespace NickFinance.PettyCash.CashCounts;

/// <summary>
/// One physical cash count by a custodian, recording the variance against
/// the system-of-record balance computed from the Ledger. Variances drive
/// fraud signals and audit follow-up.
/// </summary>
public class CashCount
{
    public Guid CashCountId { get; set; } = Guid.NewGuid();

    public Guid FloatId { get; set; }

    /// <summary>The custodian who counted (or another auditor).</summary>
    public Guid CountedByUserId { get; set; }

    /// <summary>Optional witness — second pair of eyes co-signing the count.</summary>
    public Guid? WitnessUserId { get; set; }

    public DateTimeOffset CountedAt { get; set; }

    /// <summary>The actual physical cash on hand at count time, in minor units.</summary>
    public long PhysicalAmountMinor { get; set; }

    /// <summary>The expected balance per the Ledger as of <see cref="CountedAt"/>, in minor units.</summary>
    public long SystemAmountMinor { get; set; }

    /// <summary>Convenience — physical minus system. Positive = surplus, negative = shortage.</summary>
    public long VarianceMinor => PhysicalAmountMinor - SystemAmountMinor;

    public string CurrencyCode { get; set; } = "GHS";

    /// <summary>Free-text justification when there's a variance — required by service if non-zero.</summary>
    public string? VarianceReason { get; set; }

    public long TenantId { get; set; } = 1;
}
