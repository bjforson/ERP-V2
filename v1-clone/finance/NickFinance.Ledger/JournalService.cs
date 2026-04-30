using System.Security.Cryptography;
using System.Text;
using NickERP.Platform.Identity;

namespace NickFinance.Ledger;

/// <summary>
/// Default <see cref="IJournalService"/> implementation. Validates
/// pre-flight, then defers to <see cref="ILedgerWriter"/> with
/// <c>SourceModule = "manual"</c>. Idempotency-key derivation is stable
/// across retries so the operator pressing "Post" twice never doubles
/// the entry.
/// </summary>
public sealed class JournalService : IJournalService
{
    private readonly ILedgerWriter _writer;
    private readonly IManualJournalAccountValidator _validator;
    private readonly ISecurityAuditService _audit;

    public JournalService(
        ILedgerWriter writer,
        IManualJournalAccountValidator? validator = null,
        ISecurityAuditService? audit = null)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _validator = validator ?? new PermissiveAccountValidator();
        _audit = audit ?? new NoopSecurityAuditService();
    }

    public async Task<Guid> PostManualAsync(ManualJournalRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Lines is null || req.Lines.Count < 2)
        {
            throw new MalformedLineException("A manual journal needs at least two lines.");
        }
        if (string.IsNullOrWhiteSpace(req.Narration))
        {
            throw new ArgumentException("Narration is required.", nameof(req));
        }

        long debits = 0, credits = 0;
        foreach (var l in req.Lines)
        {
            if (string.IsNullOrWhiteSpace(l.GlAccount))
                throw new MalformedLineException("Line GL account is required.");
            if (l.DebitMinor < 0 || l.CreditMinor < 0)
                throw new MalformedLineException("Debit/Credit must be non-negative.");
            var isDr = l.DebitMinor > 0;
            var isCr = l.CreditMinor > 0;
            if (isDr == isCr)
                throw new MalformedLineException("Each line must have exactly one of debit / credit non-zero.");
            checked { debits += l.DebitMinor; credits += l.CreditMinor; }
        }
        if (debits != credits)
        {
            throw new UnbalancedJournalException(debits, credits);
        }

        // Non-control / known-account guard — control accounts (1100 AR
        // control, 2000 AP control, 21xx tax payables) must not be hit
        // manually; the modules own them.
        var codes = req.Lines.Select(l => l.GlAccount).Distinct(StringComparer.Ordinal).ToArray();
        var disallowed = await _validator.ValidateAsync(codes, req.TenantId, ct);
        if (disallowed.Count > 0)
        {
            var first = disallowed[0];
            throw new ManualJournalForbiddenAccountException(first.Code, first.Reason);
        }

        var idempotencyKey = BuildIdempotencyKey(req);

        var evt = new LedgerEvent
        {
            EffectiveDate = req.EffectiveDate,
            PeriodId = req.PeriodId,
            SourceModule = "manual",
            SourceEntityType = "ManualJournal",
            SourceEntityId = idempotencyKey,
            IdempotencyKey = idempotencyKey,
            EventType = LedgerEventType.Posted,
            Narration = req.Narration,
            ActorUserId = req.ActorUserId,
            TenantId = req.TenantId,
            Lines = req.Lines.Select(l => new LedgerEventLine
            {
                AccountCode = l.GlAccount,
                DebitMinor = l.DebitMinor,
                CreditMinor = l.CreditMinor,
                CurrencyCode = l.CurrencyCode,
                Description = l.LineMemo,
            }).ToList()
        };

        var eventId = await _writer.PostAsync(evt, ct);
        await _audit.RecordAsync(
            action: SecurityAuditAction.JournalPosted,
            targetType: "manual",
            targetId: idempotencyKey,
            details: new { eventId, lines = req.Lines.Count, debits, credits },
            ct: ct);
        return eventId;
    }

    /// <summary>
    /// Stable per-request hash so two clicks of the Post button collapse
    /// into one ledger row. Includes actor + narration + every line so a
    /// subsequent edit-and-resubmit lands as a new row.
    /// </summary>
    private static string BuildIdempotencyKey(ManualJournalRequest req)
    {
        var sb = new StringBuilder();
        sb.Append("manual:")
          .Append(req.ActorUserId.ToString("N"))
          .Append(':')
          .Append(req.EffectiveDate.ToString("yyyy-MM-dd"))
          .Append(':')
          .Append(req.Narration)
          .Append('|');
        foreach (var l in req.Lines)
        {
            sb.Append(l.GlAccount).Append('=')
              .Append(l.DebitMinor).Append('/')
              .Append(l.CreditMinor).Append(':')
              .Append(l.CurrencyCode).Append(';');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var hash = SHA256.HashData(bytes);
        return "manual:" + req.ActorUserId.ToString("N") + ":" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}

/// <summary>
/// A manual journal line targeted an account the validator rejected —
/// typically a control account (AR control, AP control, tax payables).
/// </summary>
public sealed class ManualJournalForbiddenAccountException : LedgerException
{
    public string AccountCode { get; }
    public ManualJournalForbiddenAccountException(string accountCode, string reason)
        : base($"Account {accountCode} cannot be posted manually: {reason}")
    {
        AccountCode = accountCode;
    }
}
