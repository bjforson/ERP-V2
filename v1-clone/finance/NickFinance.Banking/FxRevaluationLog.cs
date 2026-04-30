namespace NickFinance.Banking;

/// <summary>
/// One revaluation observation for a (tenant, account, foreign currency, period)
/// triple. Persists the rate used so the next revaluation can take a delta from
/// it (current_rate - prior_rate) instead of restating from zero. Owned by the
/// Banking module since it sits next to the rates that drive it.
/// </summary>
/// <remarks>
/// <para>
/// Uniqueness on (tenant_id, gl_account, currency_code, period_id) means
/// re-running revaluation for the same period is idempotent at the DB layer
/// — the service catches the dupe earlier via <see cref="LedgerEventId"/>
/// lookup but the unique index is the belt-and-braces line of defence.
/// </para>
/// <para>
/// <see cref="LedgerEventId"/> points at the journal that captured the
/// gain/loss. When the journal is reversed (corrections workflow) the log
/// row stays — auditors want to see the historical rate progression even
/// across corrections; the reversed event is a separate row in the ledger.
/// </para>
/// </remarks>
public class FxRevaluationLog
{
    public Guid LogId { get; set; } = Guid.NewGuid();

    public long TenantId { get; set; } = 1;

    /// <summary>Chart-of-accounts code being revalued (e.g. "1040", "1100").</summary>
    public string GlAccount { get; set; } = string.Empty;

    /// <summary>Foreign currency the balance was held in (never the functional currency).</summary>
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>Wall-clock timestamp of the revaluation run.</summary>
    public DateTimeOffset RevaluedAt { get; set; }

    /// <summary>Effective date — the period-end the revaluation was anchored to.</summary>
    public DateOnly AsOfDate { get; set; }

    /// <summary>The accounting period this revaluation belongs to.</summary>
    public Guid PeriodId { get; set; }

    /// <summary>Rate used to translate the balance into the functional currency on this run.</summary>
    public decimal RateUsed { get; set; }

    /// <summary>Foreign-currency balance translated by <see cref="RateUsed"/> (minor units in the foreign currency).</summary>
    public long BalanceMinor { get; set; }

    /// <summary>The FK back to the journal that posted this revaluation's gain/loss.</summary>
    public Guid LedgerEventId { get; set; }
}
