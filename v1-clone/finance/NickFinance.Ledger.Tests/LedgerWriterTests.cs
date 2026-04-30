using Microsoft.EntityFrameworkCore;
using NickFinance.Ledger;
using Xunit;

namespace NickFinance.Ledger.Tests;

[Collection("Ledger")]
public class LedgerWriterTests
{
    private readonly LedgerFixture _fx;
    public LedgerWriterTests(LedgerFixture fx) => _fx = fx;

    [Fact]
    public async Task Post_RejectsSingleLineEvent()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);

        var evt = new LedgerEvent
        {
            TenantId = 1,
            EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
            PeriodId = period.PeriodId,
            IdempotencyKey = Guid.NewGuid().ToString(),
            SourceModule = "test",
            ActorUserId = Guid.NewGuid(),
            Lines =
            {
                new LedgerEventLine { AccountCode = "X", DebitMinor = 100, CurrencyCode = "GHS" }
            }
        };

        await Assert.ThrowsAsync<MalformedLineException>(() => writer.PostAsync(evt));
    }

    [Fact]
    public async Task Post_RejectsMixedCurrency()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);

        var evt = MinimalEvent(period.PeriodId);
        evt.Lines[0].CurrencyCode = "GHS";
        evt.Lines[1].CurrencyCode = "USD";

        await Assert.ThrowsAsync<MalformedLineException>(() => writer.PostAsync(evt));
    }

    [Fact]
    public async Task Post_RejectsLineWithBothDebitAndCredit()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);

        var evt = MinimalEvent(period.PeriodId);
        evt.Lines[0].DebitMinor = 100;
        evt.Lines[0].CreditMinor = 100;

        await Assert.ThrowsAsync<MalformedLineException>(() => writer.PostAsync(evt));
    }

    [Fact]
    public async Task Post_RejectsUnbalancedJournal()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);

        var evt = MinimalEvent(period.PeriodId);
        evt.Lines[0].DebitMinor = 100;
        evt.Lines[1].CreditMinor = 99;   // 1 pesewa short

        await Assert.ThrowsAsync<UnbalancedJournalException>(() => writer.PostAsync(evt));
    }

    // ------------------------------------------------------------------
    // Happy path + idempotency
    // ------------------------------------------------------------------

    [Fact]
    public async Task Post_HappyPath_PersistsEventAndLines()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);

        var evt = MinimalEvent(period.PeriodId);
        var id = await writer.PostAsync(evt);

        await using var db2 = _fx.CreateContext();
        var persisted = await db2.Events.Include(e => e.Lines).FirstAsync(e => e.EventId == id);
        Assert.Equal(2, persisted.Lines.Count);
        Assert.True(persisted.CommittedAt > DateTimeOffset.UnixEpoch);
        Assert.Equal((short)1, persisted.Lines.First(l => l.DebitMinor > 0).LineNo);
    }

    [Fact]
    public async Task Post_SameIdempotencyKey_IsNoOp()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);

        var evt1 = MinimalEvent(period.PeriodId);
        var key = evt1.IdempotencyKey;
        var id1 = await writer.PostAsync(evt1);

        var evt2 = MinimalEvent(period.PeriodId);
        evt2.IdempotencyKey = key;       // same key, different payload
        evt2.Narration = "DUPLICATE SHOULD BE IGNORED";
        var id2 = await writer.PostAsync(evt2);

        Assert.Equal(id1, id2);

        await using var db2 = _fx.CreateContext();
        var count = await db2.Events.CountAsync(e => e.IdempotencyKey == key);
        Assert.Equal(1, count);
    }

    // ------------------------------------------------------------------
    // Period lock
    // ------------------------------------------------------------------

    [Fact]
    public async Task Post_RejectedWhenPeriodHardClosed()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var periodSvc = new PeriodService(db);
        var period = await SeedOpenPeriod(db);
        await periodSvc.HardCloseAsync(period.PeriodId, Guid.NewGuid());

        await using var db2 = _fx.CreateContext();
        var writer2 = new LedgerWriter(db2);
        var evt = MinimalEvent(period.PeriodId);
        await Assert.ThrowsAsync<ClosedPeriodException>(() => writer2.PostAsync(evt));
    }

    // ------------------------------------------------------------------
    // Reversal pattern
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reverse_InvertsLegsAndNetsToZero()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var reader = new LedgerReader(db);
        var period = await SeedOpenPeriod(db);

        var evt = MinimalEvent(period.PeriodId);
        evt.Lines[0].AccountCode = "1000-CASH";
        evt.Lines[0].DebitMinor = 50_000;    // GHS 500
        evt.Lines[0].CreditMinor = 0;
        evt.Lines[1].AccountCode = "4000-REV";
        evt.Lines[1].DebitMinor = 0;
        evt.Lines[1].CreditMinor = 50_000;
        var originalId = await writer.PostAsync(evt);

        var balancePost = await reader.GetAccountBalanceAsync("1000-CASH", "GHS", evt.EffectiveDate);
        Assert.Equal(50_000, balancePost.Minor);

        await writer.ReverseAsync(
            originalId,
            period.PeriodId,
            evt.EffectiveDate,
            Guid.NewGuid(),
            "test reversal",
            "rev-" + Guid.NewGuid());

        await using var db2 = _fx.CreateContext();
        var reader2 = new LedgerReader(db2);
        var balanceAfter = await reader2.GetAccountBalanceAsync("1000-CASH", "GHS", evt.EffectiveDate);
        Assert.Equal(0, balanceAfter.Minor);
    }

    [Fact]
    public async Task Reverse_CannotDoubleReverse()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);
        var evt = MinimalEvent(period.PeriodId);
        var id = await writer.PostAsync(evt);

        await writer.ReverseAsync(id, period.PeriodId, evt.EffectiveDate,
            Guid.NewGuid(), "first", "rev-first-" + Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidReversalException>(() =>
            writer.ReverseAsync(id, period.PeriodId, evt.EffectiveDate,
                Guid.NewGuid(), "second", "rev-second-" + Guid.NewGuid()));
    }

    // ------------------------------------------------------------------
    // DB-level append-only enforcement
    // ------------------------------------------------------------------

    [Fact]
    public async Task DirectUpdateOnLedgerEvents_IsRejectedByTrigger()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);
        var id = await writer.PostAsync(MinimalEvent(period.PeriodId));

        await using var db2 = _fx.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await db2.Database.ExecuteSqlRawAsync(
                "UPDATE finance.ledger_events SET narration='hacked' WHERE event_id = {0}", id);
        });
        Assert.Contains("append-only", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectDeleteOnLedgerEvents_IsRejectedByTrigger()
    {
        await using var db = _fx.CreateContext();
        var writer = new LedgerWriter(db);
        var period = await SeedOpenPeriod(db);
        var id = await writer.PostAsync(MinimalEvent(period.PeriodId));

        await using var db2 = _fx.CreateContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await db2.Database.ExecuteSqlRawAsync(
                "DELETE FROM finance.ledger_events WHERE event_id = {0}", id);
        });
        Assert.Contains("append-only", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    // Unique period per test so tests can't accidentally reuse a closed period.
    private static int _periodCounter = 0;
    private static async Task<AccountingPeriod> SeedOpenPeriod(LedgerDbContext db)
    {
        var svc = new PeriodService(db);
        var n = Interlocked.Increment(ref _periodCounter);
        var year = 2100 + (n / 12);          // 2100, 2100+1, ... — far-future so nothing collides with property tests
        var month = (byte)((n % 12) + 1);    // 1..12
        return await svc.CreateAsync(year, month);
    }

    private static LedgerEvent MinimalEvent(Guid periodId) => new()
    {
        TenantId = 1,
        EffectiveDate = DateOnly.FromDateTime(DateTime.UtcNow),
        PeriodId = periodId,
        IdempotencyKey = Guid.NewGuid().ToString(),
        SourceModule = "test",
        SourceEntityType = "Test",
        SourceEntityId = Guid.NewGuid().ToString(),
        Narration = "unit test",
        ActorUserId = Guid.NewGuid(),
        Lines =
        {
            new LedgerEventLine { AccountCode = "1000", DebitMinor  = 100, CreditMinor = 0, CurrencyCode = "GHS" },
            new LedgerEventLine { AccountCode = "4000", DebitMinor  = 0,   CreditMinor = 100, CurrencyCode = "GHS" }
        }
    };
}
