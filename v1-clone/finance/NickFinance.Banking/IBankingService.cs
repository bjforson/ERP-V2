using Microsoft.EntityFrameworkCore;
using NickFinance.AP;
using NickFinance.AR;
using NickFinance.Ledger;

namespace NickFinance.Banking;

public interface IBankingService
{
    Task<BankAccount> UpsertAccountAsync(UpsertBankAccountRequest req, CancellationToken ct = default);

    /// <summary>Parse + persist a statement. Returns the imported header.</summary>
    Task<BankStatement> ImportStatementAsync(ImportStatementRequest req, CancellationToken ct = default);

    /// <summary>Walk unmatched transactions on an account and tag those with an exact-or-tolerant match against AP payments / AR receipts as Provisional.</summary>
    Task<int> AutoMatchAsync(Guid bankAccountId, MatchTolerance? tolerance = null, CancellationToken ct = default);

    /// <summary>Confirm a provisional or unmatched row.</summary>
    Task ConfirmMatchAsync(Guid bankTransactionId, Guid? entityId, string entityType, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Open a reconciliation session — stores the bank balance + ledger balance snapshot.</summary>
    Task<ReconciliationSession> OpenReconciliationAsync(Guid bankAccountId, DateOnly asOf, long bankBalanceMinor, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Close a session.</summary>
    Task<ReconciliationSession> CloseReconciliationAsync(Guid sessionId, Guid actorUserId, string? notes, CancellationToken ct = default);
}

public sealed record UpsertBankAccountRequest(
    string Code, string Name, string BankName, string AccountNumber,
    string LedgerAccount, string CurrencyCode = "GHS", long TenantId = 1);

public sealed record ImportStatementRequest(
    Guid BankAccountId,
    DateOnly StatementDate,
    string SourceFileName,
    string ParserName,
    byte[] Content,
    Guid ImportedByUserId,
    long TenantId = 1);

public sealed record MatchTolerance(int DateWindowDays = 2, long AmountMinorTolerance = 0);

public sealed class BankingException : Exception
{
    public BankingException(string message) : base(message) { }
}

public sealed class BankingService : IBankingService
{
    private readonly BankingDbContext _db;
    private readonly BankCsvParserRegistry _parsers;
    private readonly ApDbContext? _ap;
    private readonly ArDbContext? _ar;
    private readonly ILedgerReader _ledger;
    private readonly TimeProvider _clock;

    public BankingService(
        BankingDbContext db,
        BankCsvParserRegistry parsers,
        ILedgerReader ledger,
        ApDbContext? ap = null,
        ArDbContext? ar = null,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _parsers = parsers ?? throw new ArgumentNullException(nameof(parsers));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _ap = ap;
        _ar = ar;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<BankAccount> UpsertAccountAsync(UpsertBankAccountRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var existing = await _db.BankAccounts.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.Code == req.Code, ct);
        var now = _clock.GetUtcNow();
        if (existing is null)
        {
            var a = new BankAccount
            {
                Code = req.Code.Trim(), Name = req.Name.Trim(),
                BankName = req.BankName, AccountNumber = req.AccountNumber,
                CurrencyCode = req.CurrencyCode, LedgerAccount = req.LedgerAccount,
                IsActive = true, CreatedAt = now, TenantId = req.TenantId
            };
            _db.BankAccounts.Add(a);
            await _db.SaveChangesAsync(ct);
            return a;
        }
        existing.Name = req.Name.Trim();
        existing.BankName = req.BankName;
        existing.AccountNumber = req.AccountNumber;
        existing.LedgerAccount = req.LedgerAccount;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<BankStatement> ImportStatementAsync(ImportStatementRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.BankAccountId == req.BankAccountId && a.TenantId == req.TenantId, ct)
            ?? throw new BankingException($"Bank account {req.BankAccountId} not found.");
        var parser = _parsers.Find(req.ParserName);
        var parsed = await parser.ParseAsync(req.Content, account.CurrencyCode, ct);

        var stmt = new BankStatement
        {
            BankAccountId = account.BankAccountId,
            StatementDate = req.StatementDate,
            PeriodStart = parsed.PeriodStart ?? req.StatementDate,
            PeriodEnd = parsed.PeriodEnd ?? req.StatementDate,
            OpeningBalanceMinor = parsed.OpeningBalanceMinor,
            ClosingBalanceMinor = parsed.ClosingBalanceMinor,
            CurrencyCode = account.CurrencyCode,
            SourceFileName = req.SourceFileName,
            ParserName = parser.Name,
            ImportedAt = _clock.GetUtcNow(),
            ImportedByUserId = req.ImportedByUserId,
            TenantId = req.TenantId
        };
        _db.Statements.Add(stmt);

        foreach (var r in parsed.Rows)
        {
            _db.Transactions.Add(new BankTransaction
            {
                BankStatementId = stmt.BankStatementId,
                BankAccountId = account.BankAccountId,
                TransactionDate = r.TransactionDate,
                ValueDate = r.ValueDate,
                Description = r.Description,
                Reference = r.Reference,
                Direction = r.Direction,
                AmountMinor = r.AmountMinor,
                CurrencyCode = account.CurrencyCode,
                MatchStatus = BankMatchStatus.Unmatched,
                TenantId = req.TenantId
            });
        }

        await _db.SaveChangesAsync(ct);
        return stmt;
    }

    public async Task<int> AutoMatchAsync(Guid bankAccountId, MatchTolerance? tolerance = null, CancellationToken ct = default)
    {
        tolerance ??= new MatchTolerance();
        var unmatched = await _db.Transactions
            .Where(t => t.BankAccountId == bankAccountId && t.MatchStatus == BankMatchStatus.Unmatched)
            .ToListAsync(ct);
        if (unmatched.Count == 0) return 0;

        // Pull AP payments + AR receipts in the date range, into memory
        // (volumes are SME-scale — < 10k rows / month).
        var minDate = unmatched.Min(t => t.TransactionDate).AddDays(-tolerance.DateWindowDays);
        var maxDate = unmatched.Max(t => t.TransactionDate).AddDays(tolerance.DateWindowDays);

        var apPayments = _ap is null ? new List<ApPayment>()
            : await _ap.Payments.Where(p => p.PaymentDate >= minDate && p.PaymentDate <= maxDate).ToListAsync(ct);
        var arReceipts = _ar is null ? new List<ArReceipt>()
            : await _ar.Receipts.Where(r => r.ReceiptDate >= minDate && r.ReceiptDate <= maxDate).ToListAsync(ct);

        var matches = 0;
        foreach (var t in unmatched)
        {
            // Debit on bank statement = money OUT — typically an AP payment (cash credited).
            // Credit on bank statement = money IN — typically an AR receipt.
            if (t.Direction == BankTransactionDirection.Debit)
            {
                var hit = apPayments.FirstOrDefault(p =>
                    p.AmountMinor == t.AmountMinor + (p.AmountMinor - t.AmountMinor)   // net comparison done below
                    || (Math.Abs(p.AmountMinor - t.AmountMinor) <= tolerance.AmountMinorTolerance
                       && Math.Abs(p.PaymentDate.DayNumber - t.TransactionDate.DayNumber) <= tolerance.DateWindowDays));
                if (hit is not null)
                {
                    t.MatchStatus = BankMatchStatus.Provisional;
                    t.MatchedToEntityId = hit.ApPaymentId;
                    t.MatchedToEntityType = "ApPayment";
                    t.MatchedAt = _clock.GetUtcNow();
                    matches++;
                }
            }
            else
            {
                var hit = arReceipts.FirstOrDefault(r =>
                    Math.Abs(r.AmountMinor - t.AmountMinor) <= tolerance.AmountMinorTolerance
                    && Math.Abs(r.ReceiptDate.DayNumber - t.TransactionDate.DayNumber) <= tolerance.DateWindowDays);
                if (hit is not null)
                {
                    t.MatchStatus = BankMatchStatus.Provisional;
                    t.MatchedToEntityId = hit.ArReceiptId;
                    t.MatchedToEntityType = "ArReceipt";
                    t.MatchedAt = _clock.GetUtcNow();
                    matches++;
                }
            }
        }
        if (matches > 0) await _db.SaveChangesAsync(ct);
        return matches;
    }

    public async Task ConfirmMatchAsync(Guid bankTransactionId, Guid? entityId, string entityType, Guid actorUserId, CancellationToken ct = default)
    {
        var t = await _db.Transactions.FirstOrDefaultAsync(x => x.BankTransactionId == bankTransactionId, ct)
            ?? throw new BankingException($"Transaction {bankTransactionId} not found.");
        if (entityId is not null) t.MatchedToEntityId = entityId;
        if (!string.IsNullOrWhiteSpace(entityType)) t.MatchedToEntityType = entityType;
        t.MatchStatus = BankMatchStatus.Matched;
        t.MatchedAt = _clock.GetUtcNow();
        t.MatchedByUserId = actorUserId;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ReconciliationSession> OpenReconciliationAsync(Guid bankAccountId, DateOnly asOf, long bankBalanceMinor, Guid actorUserId, CancellationToken ct = default)
    {
        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.BankAccountId == bankAccountId, ct)
            ?? throw new BankingException($"Bank account {bankAccountId} not found.");
        var ledgerBal = await _ledger.GetAccountBalanceAsync(account.LedgerAccount, account.CurrencyCode, asOf, account.TenantId, ct);
        var sess = new ReconciliationSession
        {
            BankAccountId = bankAccountId,
            AsOfDate = asOf,
            BankBalanceMinor = bankBalanceMinor,
            LedgerBalanceMinor = ledgerBal.Minor,
            Status = ReconciliationStatus.Open,
            OpenedByUserId = actorUserId,
            OpenedAt = _clock.GetUtcNow(),
            TenantId = account.TenantId
        };
        _db.Reconciliations.Add(sess);
        await _db.SaveChangesAsync(ct);
        return sess;
    }

    public async Task<ReconciliationSession> CloseReconciliationAsync(Guid sessionId, Guid actorUserId, string? notes, CancellationToken ct = default)
    {
        var sess = await _db.Reconciliations.FirstOrDefaultAsync(s => s.ReconciliationSessionId == sessionId, ct)
            ?? throw new BankingException($"Session {sessionId} not found.");
        sess.Status = ReconciliationStatus.Closed;
        sess.ClosedAt = _clock.GetUtcNow();
        sess.ClosedByUserId = actorUserId;
        sess.Notes = notes;
        await _db.SaveChangesAsync(ct);
        return sess;
    }
}
