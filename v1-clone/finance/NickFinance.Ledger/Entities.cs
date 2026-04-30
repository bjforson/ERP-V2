namespace NickFinance.Ledger;

/// <summary>
/// A single financial fact that moves value between accounts. Immutable once
/// committed. Corrections are via a follow-up Reversal event that references
/// the original, never by mutating an existing row.
/// </summary>
public class LedgerEvent
{
    public Guid EventId { get; set; } = Guid.NewGuid();

    /// <summary>Wall-clock time the event was recorded. Monotonic, never backdated.</summary>
    public DateTimeOffset CommittedAt { get; set; }

    /// <summary>Accounting/posting date. May differ from CommittedAt for accruals.</summary>
    public DateOnly EffectiveDate { get; set; }

    /// <summary>The accounting period this event belongs to.</summary>
    public Guid PeriodId { get; set; }

    /// <summary>Which module posted this (e.g. "petty_cash", "ar", "ap", "payroll").</summary>
    public string SourceModule { get; set; } = string.Empty;
    public string SourceEntityType { get; set; } = string.Empty;
    public string SourceEntityId { get; set; } = string.Empty;

    /// <summary>
    /// Caller-supplied dedupe key. A second insert with the same key is a no-op
    /// (not an error) — matches the "at-least-once retry safely" model.
    /// Unique in the DB.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>
    /// "Posted" for a first-class event. "Reversal" for a correction that cancels
    /// another event (ReversesEventId filled in).
    /// </summary>
    public LedgerEventType EventType { get; set; } = LedgerEventType.Posted;

    /// <summary>If EventType == Reversal, the original event being undone.</summary>
    public Guid? ReversesEventId { get; set; }

    /// <summary>Free-text from the poster (max 500 chars enforced in schema).</summary>
    public string Narration { get; set; } = string.Empty;

    /// <summary>Who caused this event. Canonical identity-service user id.</summary>
    public Guid ActorUserId { get; set; }

    /// <summary>Multi-tenant isolation. Every event must carry this.</summary>
    public long TenantId { get; set; } = 1;

    public List<LedgerEventLine> Lines { get; set; } = new();
}

public enum LedgerEventType
{
    Posted = 0,
    Reversal = 1
}

/// <summary>
/// One leg of a journal. Either DebitMinor or CreditMinor is non-zero (never both).
/// Dimensions (site, project, cost centre, etc.) are first-class columns where
/// common, plus an open-ended jsonb for anything else.
/// </summary>
public class LedgerEventLine
{
    public Guid EventId { get; set; }
    public short LineNo { get; set; }

    /// <summary>Flat chart-of-accounts code (e.g. "4000-REV-SCAN").</summary>
    public string AccountCode { get; set; } = string.Empty;

    /// <summary>Debit leg in minor units. 0 if this is a credit line.</summary>
    public long DebitMinor { get; set; }

    /// <summary>Credit leg in minor units. 0 if this is a debit line.</summary>
    public long CreditMinor { get; set; }

    /// <summary>ISO-4217 3-letter currency code (e.g. "GHS").</summary>
    public string CurrencyCode { get; set; } = "GHS";

    // Common dimensions — nullable, looked up from reference tables elsewhere.
    public Guid? SiteId { get; set; }
    public string? ProjectCode { get; set; }
    public string? CostCenterCode { get; set; }

    /// <summary>Any other ad-hoc dimensions stored as jsonb. Keep small.</summary>
    public string? DimsExtraJson { get; set; }

    /// <summary>Per-line free text. Optional.</summary>
    public string? Description { get; set; }

    public LedgerEvent Event { get; set; } = null!;
}

/// <summary>
/// A calendar period (typically a month). Postings are validated against
/// its Status: OPEN allows normal posting; SOFT_CLOSED restricts to users
/// with the adjust-period permission; HARD_CLOSED rejects all posting except
/// an explicit prior-period adjustment that itself is an OPEN-period event.
/// </summary>
public class AccountingPeriod
{
    public Guid PeriodId { get; set; } = Guid.NewGuid();
    public int FiscalYear { get; set; }
    public byte MonthNumber { get; set; }       // 1..12
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public PeriodStatus Status { get; set; } = PeriodStatus.Open;
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public long TenantId { get; set; } = 1;
}

public enum PeriodStatus
{
    Open = 0,
    SoftClosed = 1,
    HardClosed = 2
}
