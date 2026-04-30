namespace NickERP.Platform.Identity;

/// <summary>
/// Contract every DbContext consults when wiring multi-tenant query
/// filters. The WebApp registers a real implementation that reads from
/// the authenticated user's row; the bootstrap CLI and offline tooling
/// register a null-returning stub so background admin queries see every
/// tenant. When <see cref="Current"/> returns <c>null</c>, the query
/// filter is bypassed (returning all tenants). When it returns a real
/// id, every DbContext rewrites <c>SELECT</c> to add
/// <c>WHERE tenant_id = ...</c>.
/// </summary>
/// <remarks>
/// Cached per-request via <c>IHttpContextAccessor</c> to avoid hitting
/// the DB more than once per circuit. The contract is deliberately tiny
/// — implementations decide caching, claim parsing, etc.
/// </remarks>
public interface ITenantAccessor
{
    /// <summary>
    /// Current tenant id, or <c>null</c> for system-level queries that
    /// should see every row.
    /// </summary>
    long? Current { get; }
}

/// <summary>
/// Stub returning <c>null</c> always — used by the bootstrap CLI, smoke
/// runner, and any test that wants to see every tenant.
/// </summary>
public sealed class NullTenantAccessor : ITenantAccessor
{
    public long? Current => null;
}

/// <summary>
/// Stub locking onto a fixed tenant id — used by tests that want to
/// exercise the filter without a full HTTP context.
/// </summary>
public sealed class FixedTenantAccessor : ITenantAccessor
{
    public FixedTenantAccessor(long tenantId) { Current = tenantId; }
    public long? Current { get; }
}
