namespace NickFinance.Ledger;

/// <summary>
/// Period-end FX revaluation. Translates every monetary foreign-currency
/// account balance into the functional currency (GHS for Nick TC-Scan v1)
/// at the period-end rate, captures the difference vs. the rate previously
/// used to translate the same balance, and posts a single balanced journal
/// against accounts <c>7100 FX revaluation gain</c> and <c>7110 FX revaluation
/// loss</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 2 of Wave 3.</b> Phase 1 (Wave 2B) shipped the read path
/// (<see cref="IFxConverter"/>); this is the write path that closes the loop.
/// </para>
/// <para>
/// <b>Idempotent.</b> Re-running for the same period is a no-op — the
/// banking-side <c>fx_revaluation_log</c> uniqueness on (tenant, account,
/// currency, period_id) guarantees a single revaluation per period+account+ccy.
/// The result reports <see cref="FxRevaluationResult.WasIdempotentNoOp"/> when
/// it short-circuits.
/// </para>
/// <para>
/// <b>Carry-rate strategy.</b> First time an account+currency is revalued the
/// service translates the full balance from zero (i.e. uses the period-end
/// rate as the delta from the implicit "untranslated" historical rate of
/// zero). Subsequent runs read the prior log row's <c>rate_used</c> and post
/// only the delta — <c>balance * (current_rate - prior_rate)</c>. The full
/// rationale lives in <c>FxRevaluationService</c>.
/// </para>
/// <para>
/// <b>Implementation lives in NickFinance.Banking</b> rather than the Ledger
/// kernel — the persisted state (<c>banking.fx_revaluation_log</c>) sits next
/// to the rates that drive it, and the Banking module already references
/// Ledger for <see cref="ILedgerWriter"/> / <see cref="ILedgerReader"/> /
/// <see cref="IFxConverter"/>. The interface lives here so consumers (period
/// close UI, tests) depend on the kernel only.
/// </para>
/// </remarks>
public interface IFxRevaluationService
{
    /// <summary>
    /// For every account in <paramref name="monetaryAccountCodes"/> that has a
    /// non-zero foreign-currency balance as of <paramref name="asOf"/>, compute
    /// the difference between the carry-rate translation and the period-end-rate
    /// translation, and post a single balanced FX revaluation journal capturing
    /// the net gain/loss to <c>7100</c>/<c>7110</c>.
    /// </summary>
    /// <param name="periodId">The accounting period the revaluation belongs to.</param>
    /// <param name="asOf">Effective date for the revaluation; usually the period end.</param>
    /// <param name="monetaryAccountCodes">CoA codes whose foreign-currency balances need translating (e.g. <c>1040</c>, <c>1100</c>).</param>
    /// <param name="actorUserId">User triggering the revaluation; recorded on the ledger event for audit.</param>
    /// <param name="tenantId">Tenant scope.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A summary of what was posted. <see cref="FxRevaluationResult.WasIdempotentNoOp"/>
    /// is <c>true</c> when an existing event for the same idempotency key was
    /// found and no new journal was created.
    /// </returns>
    /// <exception cref="MissingFxRateException">No FX rate for one of the foreign currencies on or before <paramref name="asOf"/>.</exception>
    Task<FxRevaluationResult> RevalueAsync(
        Guid periodId,
        DateOnly asOf,
        IReadOnlyList<string> monetaryAccountCodes,
        Guid actorUserId,
        long tenantId = 1,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of a single revaluation run. Empty (no foreign-currency balances)
/// runs still return a valid result with <see cref="LineCount"/> = 0 and
/// <see cref="LedgerEventId"/> = <see cref="Guid.Empty"/> — caller can rely on
/// "no exception" as the success signal.
/// </summary>
public sealed record FxRevaluationResult(
    Guid LedgerEventId,
    /// <summary>Signed; &gt;0 = gain, &lt;0 = loss, 0 = no movement.</summary>
    long NetGainOrLossMinor,
    /// <summary>The functional currency the gain/loss is denominated in (GHS for v1).</summary>
    string FunctionalCurrency,
    /// <summary>How many ledger lines made it onto the journal (zero when nothing to revalue).</summary>
    int LineCount,
    /// <summary>True when this period had already been revalued and the call short-circuited.</summary>
    bool WasIdempotentNoOp);
