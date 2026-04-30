namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// Resolves a role name on a band step (<c>line_manager</c>,
/// <c>site_supervisor</c>, <c>finance</c>, …) into the canonical user id
/// who fills that role for a given voucher.
/// </summary>
/// <remarks>
/// <para>
/// Production implementations query NickHR for the requester's manager
/// chain (<c>line_manager</c> = direct manager), the site registry for
/// the supervisor (<c>site_supervisor</c>), and the Identity service for
/// users holding scope <c>Finance.Approver</c> (<c>finance</c>). Returning
/// <see cref="Guid.Empty"/> means "no one fills that role" — the engine
/// will reject the submission.
/// </para>
/// <para>
/// Tests use <see cref="StaticApproverResolver"/> which lets the test
/// pre-baking the role -> user map.
/// </para>
/// </remarks>
public interface IApproverResolver
{
    /// <summary>
    /// Resolve a role on a voucher. Returns <see cref="Guid.Empty"/> if
    /// no user fills the role (treated as "rejected at submit").
    /// </summary>
    Guid Resolve(string role, Voucher voucher);
}

/// <summary>
/// Test-friendly resolver that returns a pre-baked map of role -> user id.
/// </summary>
public sealed class StaticApproverResolver : IApproverResolver
{
    private readonly IReadOnlyDictionary<string, Guid> _map;

    public StaticApproverResolver(IReadOnlyDictionary<string, Guid> map)
    {
        _map = map ?? throw new ArgumentNullException(nameof(map));
    }

    public Guid Resolve(string role, Voucher voucher)
    {
        ArgumentNullException.ThrowIfNull(role);
        return _map.TryGetValue(role, out var id) ? id : Guid.Empty;
    }
}
