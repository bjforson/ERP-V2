namespace NickFinance.Ledger;

/// <summary>
/// Wraps <see cref="ILedgerWriter"/> for accountant-driven adjusting and
/// closing journals. The module-driven services (PettyCash, AR, AP) post
/// straight to the writer; manual posts have to go through this service so
/// we can enforce extra rules — non-control-account check, idempotency,
/// audit log — without bloating the kernel.
/// </summary>
public interface IJournalService
{
    /// <summary>
    /// Validate balance + non-control accounts and post a single
    /// manual-source ledger event. Returns the (possibly pre-existing) event id.
    /// </summary>
    Task<Guid> PostManualAsync(ManualJournalRequest req, CancellationToken ct = default);
}

/// <summary>
/// Caller-supplied shape for a manual posting. Lines are validated
/// (non-empty, balanced, every account is non-control) before any DB I/O.
/// </summary>
public sealed record ManualJournalRequest(
    DateOnly EffectiveDate,
    Guid PeriodId,
    string Narration,
    Guid ActorUserId,
    IReadOnlyList<ManualJournalLine> Lines,
    long TenantId = 1);

/// <summary>One leg of a manual journal. Either DR or CR is non-zero.</summary>
public sealed record ManualJournalLine(
    string GlAccount,
    long DebitMinor,
    long CreditMinor,
    string CurrencyCode = "GHS",
    string? LineMemo = null);

/// <summary>
/// Account-existence + non-control gate for manual journals. Implemented
/// outside the Ledger (in the WebApp host) over <c>ICoaService</c> so the
/// Ledger doesn't take a cross-module reference. The default
/// (<see cref="PermissiveAccountValidator"/>) is wired by tests / tooling
/// that don't have a CoA loaded — it accepts every code as non-control.
/// </summary>
public interface IManualJournalAccountValidator
{
    /// <summary>
    /// Returns the disallowed accounts from the input set, paired with a
    /// reason. An empty result means every code is OK to post manually.
    /// </summary>
    Task<IReadOnlyList<(string Code, string Reason)>> ValidateAsync(
        IReadOnlyCollection<string> accountCodes,
        long tenantId,
        CancellationToken ct = default);
}

/// <summary>Test/no-op default — accepts every code as a valid non-control.</summary>
public sealed class PermissiveAccountValidator : IManualJournalAccountValidator
{
    public Task<IReadOnlyList<(string Code, string Reason)>> ValidateAsync(
        IReadOnlyCollection<string> accountCodes,
        long tenantId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string Code, string Reason)>>(
            Array.Empty<(string, string)>());
}
