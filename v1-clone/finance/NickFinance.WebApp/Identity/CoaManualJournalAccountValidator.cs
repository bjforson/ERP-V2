using NickFinance.Coa;
using NickFinance.Ledger;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Production wiring of <see cref="IManualJournalAccountValidator"/>:
/// looks up each code in the Chart of Accounts and rejects any that is
/// missing, retired, or marked <c>IsControl</c>.
/// </summary>
/// <remarks>
/// Lives in the WebApp host so the Ledger doesn't have to take a CoA
/// reference. Tests / the bootstrap CLI register
/// <see cref="PermissiveAccountValidator"/> instead.
/// </remarks>
public sealed class CoaManualJournalAccountValidator : IManualJournalAccountValidator
{
    private readonly ICoaService _coa;

    public CoaManualJournalAccountValidator(ICoaService coa)
    {
        _coa = coa ?? throw new ArgumentNullException(nameof(coa));
    }

    public async Task<IReadOnlyList<(string Code, string Reason)>> ValidateAsync(
        IReadOnlyCollection<string> accountCodes,
        long tenantId,
        CancellationToken ct = default)
    {
        var bad = new List<(string, string)>();
        foreach (var code in accountCodes)
        {
            var acct = await _coa.FindAsync(code, tenantId, ct);
            if (acct is null)
            {
                bad.Add((code, "account does not exist in CoA"));
                continue;
            }
            if (!acct.IsActive)
            {
                bad.Add((code, "account is retired"));
                continue;
            }
            if (acct.IsControl)
            {
                bad.Add((code, "control account — must be posted by a module, not manually"));
            }
        }
        return bad;
    }
}
