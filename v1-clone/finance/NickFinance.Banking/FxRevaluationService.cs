using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;

namespace NickFinance.Banking;

/// <summary>
/// Default <see cref="IFxRevaluationService"/>. Lives in Banking — not the
/// Ledger kernel — because:
/// <list type="bullet">
///   <item>The persisted prior-rate state (<c>banking.fx_revaluation_log</c>)
///         is owned by Banking and would otherwise drag a Ledger→Banking
///         dependency back through the kernel.</item>
///   <item>Banking already references Ledger and CoA, so it can pull in
///         <see cref="ILedgerReader"/>, <see cref="ILedgerWriter"/>, and
///         <see cref="IFxConverter"/> without any extra coupling.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Functional currency.</b> Hardcoded to GHS for Nick TC-Scan v1; the field
/// surfaces in <see cref="FxRevaluationResult.FunctionalCurrency"/> so a future
/// per-tenant override only changes one constructor argument.
/// </para>
/// <para>
/// <b>Carry-rate strategy.</b> The first time an account+currency pair is
/// revalued there is no prior log row, so the delta is the full
/// <c>balance_minor * current_rate</c>. This is mathematically correct: the
/// "implicit prior translation" was zero (the GL has the foreign balance but
/// no GHS shadow yet). Subsequent runs read the latest log row and post only
/// <c>balance_minor * (current_rate - prior_rate)</c> — pure mark-to-market
/// drift. The log is not retroactively rewritten when the underlying balance
/// changes between runs; the next revaluation just translates the new balance
/// at the new rate and the old row stays as the historical anchor.
/// </para>
/// <para>
/// <b>Idempotency.</b> Two layers — the kernel <see cref="ILedgerWriter"/>
/// dedupes on <see cref="LedgerEvent.IdempotencyKey"/> shaped
/// <c>fxreval:&lt;periodId&gt;:&lt;asOf&gt;</c>, and the
/// <c>fx_revaluation_log</c> table has a unique index on
/// (tenant, account, currency, period_id). A repeat call short-circuits at
/// the writer and returns <see cref="FxRevaluationResult.WasIdempotentNoOp"/>
/// = <c>true</c>.
/// </para>
/// <para>
/// <b>Posting model.</b> All ledger lines are in the functional currency (GHS),
/// per kernel rule "no mixed currencies on one event". The original foreign
/// balance stays untouched in its native currency; the FX revaluation journal
/// adds a GHS shadow that lifts the account's GHS-equivalent balance to
/// today's translation. The trial-balance-by-currency view shows both legs.
/// </para>
/// </remarks>
public sealed class FxRevaluationService : IFxRevaluationService
{
    public const string FunctionalCurrency = "GHS";
    public const string GainAccount = "7100";
    public const string LossAccount = "7110";

    private readonly ILedgerReader _reader;
    private readonly ILedgerWriter _writer;
    private readonly IFxConverter _converter;
    private readonly LedgerDbContext _ledger;
    private readonly BankingDbContext _banking;
    private readonly TimeProvider _clock;

    public FxRevaluationService(
        ILedgerReader reader,
        ILedgerWriter writer,
        IFxConverter converter,
        LedgerDbContext ledger,
        BankingDbContext banking,
        TimeProvider? clock = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _banking = banking ?? throw new ArgumentNullException(nameof(banking));
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<FxRevaluationResult> RevalueAsync(
        Guid periodId,
        DateOnly asOf,
        IReadOnlyList<string> monetaryAccountCodes,
        Guid actorUserId,
        long tenantId = 1,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monetaryAccountCodes);

        var idempotencyKey = $"fxreval:{periodId:N}:{asOf:yyyy-MM-dd}";

        // Layer 1 idempotency — if an event with the same key already exists,
        // skip the entire pipeline (rate lookup, log writes, journal build).
        // The kernel writer would catch the dupe a second time below, but
        // short-circuiting here saves a round-trip to FxConverter and avoids
        // a partial write of fx_revaluation_log on a no-op call.
        var alreadyPostedEventId = await _ledger.Events
            .AsNoTracking()
            .Where(e => e.IdempotencyKey == idempotencyKey && e.TenantId == tenantId)
            .Select(e => (Guid?)e.EventId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (alreadyPostedEventId is { } alreadyId)
        {
            return new FxRevaluationResult(
                LedgerEventId: alreadyId,
                NetGainOrLossMinor: 0,
                FunctionalCurrency: FunctionalCurrency,
                LineCount: 0,
                WasIdempotentNoOp: true);
        }

        // Build the journal. We collect all the prepared deltas first, post a
        // single combined event, then write the log rows once we know the
        // event id. Any rate-missing exception aborts before any write.
        var prepared = new List<RevalLine>();

        foreach (var rawCode in monetaryAccountCodes)
        {
            if (string.IsNullOrWhiteSpace(rawCode)) continue;
            var code = rawCode.Trim();

            var balances = await _reader
                .GetAccountBalancesByCurrencyAsync(code, asOf, tenantId, ct)
                .ConfigureAwait(false);

            foreach (var (currency, balanceMinor) in balances)
            {
                // Functional-currency balances need no translation (and are
                // skipped before hitting the converter to avoid a no-op rate
                // lookup).
                if (string.Equals(currency, FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (balanceMinor == 0) continue;

                var rate = await _converter
                    .GetRateAsync(currency, FunctionalCurrency, asOf, tenantId, ct)
                    .ConfigureAwait(false)
                    ?? throw new MissingFxRateException(currency, FunctionalCurrency, asOf, tenantId);

                // Prior rate: most recent log row for (tenant, account, ccy)
                // BEFORE this period. Same period would be the no-op path
                // above; we filter it out defensively here too.
                var prior = await _banking.FxRevaluationLogs
                    .AsNoTracking()
                    .Where(l => l.TenantId == tenantId
                             && l.GlAccount == code
                             && l.CurrencyCode == currency
                             && l.PeriodId != periodId)
                    .OrderByDescending(l => l.AsOfDate)
                    .ThenByDescending(l => l.RevaluedAt)
                    .Select(l => (decimal?)l.RateUsed)
                    .FirstOrDefaultAsync(ct);

                var rateDelta = rate - (prior ?? 0m);
                if (rateDelta == 0m) continue;     // rate hasn't moved; nothing to post

                // balance * rate_delta — banker's rounding to whole minor
                // units. Sign survives: positive delta on a positive balance
                // = gain (DR account, CR 7100); negative delta on a positive
                // balance = loss (CR account, DR 7110); for a credit-natural
                // account (e.g. AP control 2010 with a CR balance, balanceMinor < 0)
                // the polarity inverts naturally.
                var deltaGhs = decimal.Round(balanceMinor * rateDelta, 0, MidpointRounding.ToEven);
                if (deltaGhs == 0m) continue;
                var deltaMinor = (long)deltaGhs;

                prepared.Add(new RevalLine(
                    AccountCode: code,
                    Currency: currency,
                    BalanceMinor: balanceMinor,
                    RateUsed: rate,
                    DeltaMinor: deltaMinor));
            }
        }

        if (prepared.Count == 0)
        {
            // Nothing to revalue — return a valid empty result. We don't post
            // an empty event (the writer would reject it as <2 lines), and we
            // don't write log rows because there's no rate to anchor them to.
            return new FxRevaluationResult(
                LedgerEventId: Guid.Empty,
                NetGainOrLossMinor: 0,
                FunctionalCurrency: FunctionalCurrency,
                LineCount: 0,
                WasIdempotentNoOp: false);
        }

        var lines = new List<LedgerEventLine>(prepared.Count * 2);
        long net = 0;
        foreach (var p in prepared)
        {
            net += p.DeltaMinor;
            if (p.DeltaMinor > 0)
            {
                // Gain — DR account, CR 7100.
                lines.Add(new LedgerEventLine
                {
                    AccountCode = p.AccountCode,
                    DebitMinor = p.DeltaMinor,
                    CreditMinor = 0,
                    CurrencyCode = FunctionalCurrency,
                    Description = $"FX reval gain on {p.Currency} balance @ {p.RateUsed:0.########}"
                });
                lines.Add(new LedgerEventLine
                {
                    AccountCode = GainAccount,
                    DebitMinor = 0,
                    CreditMinor = p.DeltaMinor,
                    CurrencyCode = FunctionalCurrency,
                    Description = $"FX reval gain — {p.AccountCode} ({p.Currency})"
                });
            }
            else
            {
                // Loss — DR 7110, CR account. DeltaMinor is negative; flip sign.
                var abs = -p.DeltaMinor;
                lines.Add(new LedgerEventLine
                {
                    AccountCode = LossAccount,
                    DebitMinor = abs,
                    CreditMinor = 0,
                    CurrencyCode = FunctionalCurrency,
                    Description = $"FX reval loss — {p.AccountCode} ({p.Currency})"
                });
                lines.Add(new LedgerEventLine
                {
                    AccountCode = p.AccountCode,
                    DebitMinor = 0,
                    CreditMinor = abs,
                    CurrencyCode = FunctionalCurrency,
                    Description = $"FX reval loss on {p.Currency} balance @ {p.RateUsed:0.########}"
                });
            }
        }

        var evt = new LedgerEvent
        {
            TenantId = tenantId,
            EffectiveDate = asOf,
            PeriodId = periodId,
            SourceModule = "fx_revaluation",
            SourceEntityType = "FxRevaluation",
            SourceEntityId = $"{periodId:N}-{asOf:yyyyMMdd}",
            IdempotencyKey = idempotencyKey,
            EventType = LedgerEventType.Posted,
            Narration = $"FX revaluation as of {asOf:yyyy-MM-dd} ({prepared.Count} currency pair(s))",
            ActorUserId = actorUserId,
            Lines = lines
        };

        var eventId = await _writer.PostAsync(evt, ct).ConfigureAwait(false);

        // Write log rows. We use AddRange + a single SaveChanges so the unique
        // index protects us if two revaluations race for the same period.
        var now = _clock.GetUtcNow();
        foreach (var p in prepared)
        {
            _banking.FxRevaluationLogs.Add(new FxRevaluationLog
            {
                TenantId = tenantId,
                GlAccount = p.AccountCode,
                CurrencyCode = p.Currency,
                RevaluedAt = now,
                AsOfDate = asOf,
                PeriodId = periodId,
                RateUsed = p.RateUsed,
                BalanceMinor = p.BalanceMinor,
                LedgerEventId = eventId
            });
        }
        await _banking.SaveChangesAsync(ct).ConfigureAwait(false);

        return new FxRevaluationResult(
            LedgerEventId: eventId,
            NetGainOrLossMinor: net,
            FunctionalCurrency: FunctionalCurrency,
            LineCount: lines.Count,
            WasIdempotentNoOp: false);
    }

    private readonly record struct RevalLine(
        string AccountCode,
        string Currency,
        long BalanceMinor,
        decimal RateUsed,
        long DeltaMinor);
}
