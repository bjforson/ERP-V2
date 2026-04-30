using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using Xunit;

namespace NickFinance.Ledger.Tests;

/// <summary>
/// Property-based tests — generate hundreds of random journals and
/// assert the writer either accepts (and balance/idempotency invariants
/// hold) or rejects with the right exception. Uses a seeded Random so
/// failures reproduce deterministically.
/// </summary>
[Collection("Ledger")]
public class LedgerPropertyTests
{
    private readonly LedgerFixture _fx;
    public LedgerPropertyTests(LedgerFixture fx) => _fx = fx;

    // Seed chosen deterministically so reruns repro. Override with env if needed.
    private static int Seed =>
        int.TryParse(Environment.GetEnvironmentVariable("NICKFINANCE_TEST_SEED"), out var s) ? s : 20260424;

    [Fact]
    public async Task Post_1000_RandomBalancedJournals_AllAccepted()
    {
        const int N = 1000;
        var rng = new Random(Seed);

        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await new PeriodService(db).CreateAsync(2026, 4);

        var accepted = 0;
        for (int i = 0; i < N; i++)
        {
            var evt = GenerateBalancedEvent(rng, period.PeriodId);
            try
            {
                await writer.PostAsync(evt);
                accepted++;
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Random journal #{i} was rejected with {ex.GetType().Name}: {ex.Message}. " +
                    $"Seed={Seed}. Dump: {DumpEvent(evt)}");
            }
        }

        Assert.Equal(N, accepted);

        // Cross-check: for every account we touched, the system-wide
        // dr-cr over all events must be zero (trial balance balances).
        await using var db2 = _fx.CreateContext();
        var totals = await db2.EventLines
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Drs = g.Sum(x => (long?)x.DebitMinor) ?? 0L,
                Crs = g.Sum(x => (long?)x.CreditMinor) ?? 0L
            })
            .FirstAsync();
        Assert.Equal(totals.Drs, totals.Crs);
    }

    [Fact]
    public async Task Post_500_RandomUnbalancedJournals_AllRejected()
    {
        const int N = 500;
        var rng = new Random(Seed + 1);

        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await new PeriodService(db).CreateAsync(2026, 5);

        for (int i = 0; i < N; i++)
        {
            var evt = GenerateBalancedEvent(rng, period.PeriodId);
            // Introduce a 1-pesewa imbalance on a random credit line.
            var creditLine = evt.Lines.First(l => l.CreditMinor > 0);
            creditLine.CreditMinor += rng.Next(1, 10);

            var ex = await Assert.ThrowsAsync<UnbalancedJournalException>(() => writer.PostAsync(evt));
            Assert.True(ex.DebitsMinor != ex.CreditsMinor);
        }

        // And zero events should have been posted.
        await using var db2 = _fx.CreateContext();
        var count = await db2.Events.CountAsync(e => e.PeriodId == period.PeriodId);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Post_100_DuplicateIdempotencyKeys_OnlyFirstPersisted()
    {
        const int N = 100;
        var rng = new Random(Seed + 2);

        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await new PeriodService(db).CreateAsync(2026, 6);

        for (int i = 0; i < N; i++)
        {
            var key = $"idempo-{i:0000}-{Guid.NewGuid():N}";
            var a = GenerateBalancedEvent(rng, period.PeriodId); a.IdempotencyKey = key;
            var b = GenerateBalancedEvent(rng, period.PeriodId); b.IdempotencyKey = key;
            var c = GenerateBalancedEvent(rng, period.PeriodId); c.IdempotencyKey = key;

            var id1 = await writer.PostAsync(a);
            var id2 = await writer.PostAsync(b);
            var id3 = await writer.PostAsync(c);

            Assert.Equal(id1, id2);
            Assert.Equal(id1, id3);
        }

        await using var db2 = _fx.CreateContext();
        var count = await db2.Events.CountAsync(e => e.PeriodId == period.PeriodId);
        Assert.Equal(N, count);
    }

    // ------------------------------------------------------------------
    // Random-journal generator
    // ------------------------------------------------------------------

    private static string[] _accounts = new[]
    {
        "1000-CASH-TEMA", "1010-CASH-KOTOKA", "1020-CASH-TAKORADI",
        "1100-BANK-GCB", "1110-BANK-ECOBANK",
        "1200-ACCOUNTS-RECEIVABLE", "2000-ACCOUNTS-PAYABLE",
        "2100-VAT-OUTPUT", "2110-NHIL", "2120-GETFUND", "2130-COVID",
        "4000-REV-SCAN", "4010-REV-CONSULTING",
        "5000-EXP-FUEL", "5010-EXP-TRANSPORT", "5020-EXP-WELFARE"
    };

    /// <summary>
    /// Generates a random balanced event: 2..6 DR lines + 2..6 CR lines
    /// whose totals match exactly. Values up to GHS 10M in pesewa.
    /// </summary>
    private static LedgerEvent GenerateBalancedEvent(Random rng, Guid periodId)
    {
        var drCount = rng.Next(1, 4);  // 1-3 debit lines
        var crCount = rng.Next(1, 4);  // 1-3 credit lines

        var drAmounts = new long[drCount];
        for (int i = 0; i < drCount; i++)
            drAmounts[i] = rng.NextInt64(100, 1_000_000_000L); // 1 pesewa to GHS 10M

        var total = drAmounts.Sum();

        // Split `total` across crCount lines. Use integer math so no drift.
        var crAmounts = new long[crCount];
        var remaining = total;
        for (int i = 0; i < crCount - 1; i++)
        {
            crAmounts[i] = rng.NextInt64(1, Math.Max(2, remaining - (crCount - i - 1)));
            remaining -= crAmounts[i];
        }
        crAmounts[crCount - 1] = remaining;

        var evt = new LedgerEvent
        {
            TenantId = 1,
            EffectiveDate = new DateOnly(2026, 4, rng.Next(1, 29)),
            PeriodId = periodId,
            IdempotencyKey = Guid.NewGuid().ToString(),
            SourceModule = "propertytest",
            SourceEntityType = "RandomEvent",
            SourceEntityId = Guid.NewGuid().ToString(),
            Narration = "random balanced event",
            ActorUserId = Guid.NewGuid()
        };

        foreach (var amt in drAmounts)
            evt.Lines.Add(new LedgerEventLine
            {
                AccountCode = _accounts[rng.Next(_accounts.Length)],
                DebitMinor = amt, CreditMinor = 0, CurrencyCode = "GHS"
            });
        foreach (var amt in crAmounts)
            evt.Lines.Add(new LedgerEventLine
            {
                AccountCode = _accounts[rng.Next(_accounts.Length)],
                DebitMinor = 0, CreditMinor = amt, CurrencyCode = "GHS"
            });

        return evt;
    }

    private static string DumpEvent(LedgerEvent e)
    {
        var lines = string.Join("; ", e.Lines.Select(l =>
            $"{l.AccountCode} DR={l.DebitMinor} CR={l.CreditMinor}"));
        return $"Event[{e.IdempotencyKey}] Lines: {lines}";
    }
}
