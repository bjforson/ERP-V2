namespace NickFinance.Banking;

/// <summary>One bank or MoMo account the business holds.</summary>
public class BankAccount
{
    public Guid BankAccountId { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string BankName { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string CurrencyCode { get; set; } = "GHS";
    /// <summary>Linked Ledger account (e.g. <c>1030</c>). All transactions on this bank account post to / from this code.</summary>
    public string LedgerAccount { get; set; } = "1030";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public long TenantId { get; set; } = 1;
}

/// <summary>One statement uploaded by the user — header for many <see cref="BankTransaction"/> rows.</summary>
public class BankStatement
{
    public Guid BankStatementId { get; set; } = Guid.NewGuid();
    public Guid BankAccountId { get; set; }
    public DateOnly StatementDate { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public long OpeningBalanceMinor { get; set; }
    public long ClosingBalanceMinor { get; set; }
    public string CurrencyCode { get; set; } = "GHS";
    public string SourceFileName { get; set; } = string.Empty;
    public string ParserName { get; set; } = string.Empty;
    public DateTimeOffset ImportedAt { get; set; }
    public Guid ImportedByUserId { get; set; }
    public long TenantId { get; set; } = 1;
}

/// <summary>One row from a bank statement.</summary>
public class BankTransaction
{
    public Guid BankTransactionId { get; set; } = Guid.NewGuid();
    public Guid BankStatementId { get; set; }
    public Guid BankAccountId { get; set; }
    public DateOnly TransactionDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public BankTransactionDirection Direction { get; set; }
    public long AmountMinor { get; set; }   // always positive; sign comes from Direction
    public string CurrencyCode { get; set; } = "GHS";
    public BankMatchStatus MatchStatus { get; set; } = BankMatchStatus.Unmatched;
    /// <summary>For Matched/Provisional rows, the AP payment / AR receipt id this row reconciles against.</summary>
    public Guid? MatchedToEntityId { get; set; }
    public string? MatchedToEntityType { get; set; }   // "ApPayment", "ArReceipt", "Manual"
    public DateTimeOffset? MatchedAt { get; set; }
    public Guid? MatchedByUserId { get; set; }
    public long TenantId { get; set; } = 1;
}

public enum BankTransactionDirection
{
    Credit = 0,    // money in
    Debit = 1      // money out
}

public enum BankMatchStatus
{
    Unmatched = 0,
    Provisional = 1,    // auto-matched, awaiting human confirm
    Matched = 2,        // confirmed
    Ignored = 3         // explicitly excluded from recon (e.g. bank charges already booked)
}

/// <summary>One reconciliation session — a snapshot of who confirmed which matches and when.</summary>
public class ReconciliationSession
{
    public Guid ReconciliationSessionId { get; set; } = Guid.NewGuid();
    public Guid BankAccountId { get; set; }
    public DateOnly AsOfDate { get; set; }
    public long BankBalanceMinor { get; set; }
    public long LedgerBalanceMinor { get; set; }
    public long DifferenceMinor => BankBalanceMinor - LedgerBalanceMinor;
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.Open;
    public string? Notes { get; set; }
    public Guid OpenedByUserId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public Guid? ClosedByUserId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public long TenantId { get; set; } = 1;
}

public enum ReconciliationStatus
{
    Open = 0,
    Closed = 1,
    Reopened = 2
}
