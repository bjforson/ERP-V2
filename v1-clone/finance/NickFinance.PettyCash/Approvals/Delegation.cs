namespace NickFinance.PettyCash.Approvals;

/// <summary>
/// One delegation row — when <see cref="UserId"/> is away between
/// <see cref="ValidFromUtc"/> and <see cref="ValidUntilUtc"/>, approver
/// resolution substitutes <see cref="DelegateUserId"/> for any role that
/// would otherwise resolve to <see cref="UserId"/>.
/// </summary>
public class ApprovalDelegation
{
    public Guid ApprovalDelegationId { get; set; } = Guid.NewGuid();

    /// <summary>The user who is away.</summary>
    public Guid UserId { get; set; }

    /// <summary>The user covering for them.</summary>
    public Guid DelegateUserId { get; set; }

    public DateTimeOffset ValidFromUtc { get; set; }
    public DateTimeOffset ValidUntilUtc { get; set; }

    /// <summary>Free-text reason — "annual leave", "sick", etc. Audit only.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public long TenantId { get; set; } = 1;
}

/// <summary>
/// Wraps an inner <see cref="IApproverResolver"/> and applies active
/// delegations on top. When the inner resolver returns a user X, this
/// resolver checks <see cref="ApprovalDelegation"/> rows for X covering
/// the current wall-clock; if any match, returns the delegate instead.
/// </summary>
/// <remarks>
/// Delegation chains are followed up to 5 hops to prevent infinite loops
/// (A delegated to B, B delegated to C, ...). Past 5 hops the resolver
/// returns the last reachable user with a warning logged via the host's
/// logging stack.
/// </remarks>
public sealed class DelegatingApproverResolver : IApproverResolver
{
    private const int MaxHops = 5;
    private readonly IApproverResolver _inner;
    private readonly IReadOnlyList<ApprovalDelegation> _activeDelegations;
    private readonly TimeProvider _clock;

    public DelegatingApproverResolver(
        IApproverResolver inner,
        IReadOnlyList<ApprovalDelegation> activeDelegations,
        TimeProvider? clock = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _activeDelegations = activeDelegations ?? throw new ArgumentNullException(nameof(activeDelegations));
        _clock = clock ?? TimeProvider.System;
    }

    public Guid Resolve(string role, Voucher voucher)
    {
        var resolved = _inner.Resolve(role, voucher);
        if (resolved == Guid.Empty) return Guid.Empty;

        var now = _clock.GetUtcNow();
        var seen = new HashSet<Guid> { resolved };
        for (var hop = 0; hop < MaxHops; hop++)
        {
            var match = _activeDelegations.FirstOrDefault(d =>
                d.UserId == resolved &&
                d.ValidFromUtc <= now &&
                now <= d.ValidUntilUtc &&
                d.TenantId == voucher.TenantId);
            if (match is null) break;
            resolved = match.DelegateUserId;
            if (!seen.Add(resolved)) break;   // cycle — bail
        }
        return resolved;
    }
}
