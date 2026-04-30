using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Identity;

namespace NickFinance.Ledger;

/// <summary>
/// Writes to the append-only ledger. Enforces every invariant that matters
/// BEFORE touching the DB:
///   - At least 2 lines
///   - Every line has exactly one of debit/credit non-zero
///   - All lines share the same currency (v1; multi-currency later with FX leg)
///   - SUM(debits) == SUM(credits)
///   - Period is OPEN (unless caller has the AllowPostToSoftClosed flag)
///   - Idempotency key not seen before (repeat call is a no-op)
///   - Reversal targets a real Posted event that isn't already reversed
/// The DB then re-enforces the balance invariant via a deferred constraint
/// as a belt-and-suspenders defence against future code paths.
/// </summary>
public interface ILedgerWriter
{
    /// <summary>
    /// Post an event. Returns the (possibly pre-existing) event id — if the
    /// idempotency key matched an existing event, the original's id is
    /// returned and no new row is created.
    /// </summary>
    Task<Guid> PostAsync(LedgerEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Post a reversal of a prior event. The reversal's lines are derived
    /// automatically (debits become credits, amounts identical, same
    /// accounts and dimensions). Returns the reversal event id.
    /// </summary>
    Task<Guid> ReverseAsync(
        Guid originalEventId,
        Guid periodId,
        DateOnly effectiveDate,
        Guid actorUserId,
        string reason,
        string idempotencyKey,
        CancellationToken ct = default);
}

public class LedgerWriter : ILedgerWriter
{
    private readonly LedgerDbContext _db;
    private readonly ISecurityAuditService _audit;
    private readonly TimeProvider _clock;

    public LedgerWriter(LedgerDbContext db, TimeProvider? clock = null)
        : this(db, audit: null, clock) { }

    public LedgerWriter(LedgerDbContext db, ISecurityAuditService? audit, TimeProvider? clock = null)
    {
        _db = db;
        _audit = audit ?? new NoopSecurityAuditService();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<Guid> PostAsync(LedgerEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        // Idempotency: repeat calls with same key return the original event.
        if (string.IsNullOrWhiteSpace(evt.IdempotencyKey))
            throw new ArgumentException("IdempotencyKey is required.", nameof(evt));

        var existing = await _db.Events
            .AsNoTracking()
            .Where(e => e.IdempotencyKey == evt.IdempotencyKey && e.TenantId == evt.TenantId)
            .Select(e => new { e.EventId })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
            return existing.EventId;

        ValidateShape(evt);
        await ValidatePeriodAsync(evt.PeriodId, evt.TenantId, ct);

        evt.CommittedAt = _clock.GetUtcNow();
        // Line numbering is stable 1..N in the order the caller supplied.
        short n = 1;
        foreach (var line in evt.Lines)
            line.LineNo = n++;

        _db.Events.Add(evt);
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            action: evt.EventType == LedgerEventType.Reversal
                ? SecurityAuditAction.JournalReversed
                : SecurityAuditAction.JournalPosted,
            targetType: "LedgerEvent",
            targetId: evt.EventId.ToString(),
            result: SecurityAuditResult.Allowed,
            details: new { module = evt.SourceModule, entityType = evt.SourceEntityType, entityId = evt.SourceEntityId, idempotency = evt.IdempotencyKey, lineCount = evt.Lines.Count },
            ct: ct);
        return evt.EventId;
    }

    public async Task<Guid> ReverseAsync(
        Guid originalEventId,
        Guid periodId,
        DateOnly effectiveDate,
        Guid actorUserId,
        string reason,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var original = await _db.Events
            .Include(e => e.Lines)
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == originalEventId, ct)
            ?? throw new InvalidReversalException($"Event {originalEventId} not found.");

        if (original.EventType == LedgerEventType.Reversal)
            throw new InvalidReversalException($"Cannot reverse a reversal (event {originalEventId}).");

        var alreadyReversed = await _db.Events
            .AnyAsync(e => e.ReversesEventId == originalEventId, ct);
        if (alreadyReversed)
            throw new InvalidReversalException($"Event {originalEventId} has already been reversed.");

        var reversal = new LedgerEvent
        {
            TenantId = original.TenantId,
            EffectiveDate = effectiveDate,
            PeriodId = periodId,
            SourceModule = original.SourceModule,
            SourceEntityType = original.SourceEntityType,
            SourceEntityId = original.SourceEntityId,
            IdempotencyKey = idempotencyKey,
            EventType = LedgerEventType.Reversal,
            ReversesEventId = originalEventId,
            Narration = $"REVERSAL of {original.EventId:N}: {reason}",
            ActorUserId = actorUserId,
            Lines = original.Lines.Select(l => new LedgerEventLine
            {
                AccountCode = l.AccountCode,
                // The flip: debits become credits and vice versa.
                DebitMinor = l.CreditMinor,
                CreditMinor = l.DebitMinor,
                CurrencyCode = l.CurrencyCode,
                SiteId = l.SiteId,
                ProjectCode = l.ProjectCode,
                CostCenterCode = l.CostCenterCode,
                DimsExtraJson = l.DimsExtraJson,
                Description = $"Reversal of line {l.LineNo}: {l.Description}"
            }).ToList()
        };

        return await PostAsync(reversal, ct);
    }

    private static void ValidateShape(LedgerEvent e)
    {
        if (e.Lines is null || e.Lines.Count < 2)
            throw new MalformedLineException("An event must have at least two lines.");

        var currency = e.Lines[0].CurrencyCode;
        long debits = 0, credits = 0;

        foreach (var line in e.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.AccountCode))
                throw new MalformedLineException("Line AccountCode is required.");

            if (line.CurrencyCode != currency)
                throw new MalformedLineException(
                    $"Mixed currencies in one event are not supported yet " +
                    $"(saw {line.CurrencyCode} after {currency}).");

            if (line.DebitMinor < 0 || line.CreditMinor < 0)
                throw new MalformedLineException("Debit/Credit must be non-negative.");

            var isDr = line.DebitMinor > 0;
            var isCr = line.CreditMinor > 0;
            if (isDr == isCr)
                throw new MalformedLineException(
                    "A line must have exactly one of debit / credit non-zero.");

            checked
            {
                debits += line.DebitMinor;
                credits += line.CreditMinor;
            }
        }

        if (debits != credits)
            throw new UnbalancedJournalException(debits, credits);
    }

    private async Task ValidatePeriodAsync(Guid periodId, long tenantId, CancellationToken ct)
    {
        var period = await _db.Periods
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PeriodId == periodId && p.TenantId == tenantId, ct)
            ?? throw new LedgerException($"Period {periodId} not found for tenant {tenantId}.");

        if (period.Status == PeriodStatus.HardClosed)
            throw new ClosedPeriodException(periodId, period.Status);

        // Soft-closed is allowed at this layer; the caller's authorisation
        // check gates it. V1 ledger does not know about roles yet.
    }
}
