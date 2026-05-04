namespace NickHR.WebApp.Identity;

/// <summary>
/// Local mirror of the segregation-of-duties contract that Phase 1 ships
/// alongside the 15-role catalog. Held inside <c>NickHR.WebApp.Identity</c>
/// so the form components can inject + call it without taking a project
/// reference on <c>NickFinance.WebApp</c> (which would invert the
/// dependency direction — NickHR is upstream of NickFinance, not the other
/// way around).
/// </summary>
/// <remarks>
/// <para>
/// When Phase 1 lands the canonical interface in
/// <c>platform/NickERP.Platform.Identity</c>, this file should be deleted
/// and the section component switched to the platform copy. Until then,
/// a default no-op implementation is registered so the form never crashes
/// on a missing service in environments where Phase 1's wiring isn't
/// active yet (e.g. dev rebuilds during the parallel rollout).
/// </para>
/// <para>
/// Two surface areas:
/// <list type="bullet">
///   <item><description>
///     <see cref="GetWarningsAsync"/> — soft warnings shown inline as a
///     yellow MudAlert. Save still allowed (banner just informs the
///     admin).
///   </description></item>
///   <item><description>
///     <see cref="SodViolationException"/> — thrown by the upstream
///     <c>GrantRoleAsync</c> for HARD forbidden pairs (e.g. SiteApprover
///     + SiteCustodian for the same site). The section catches it and
///     surfaces a red MudAlert; the parent form treats this as a save
///     failure.
///   </description></item>
/// </list>
/// </para>
/// </remarks>
public interface ISodService
{
    /// <summary>
    /// Returns the list of human-readable SoD warnings (soft, save-allowed)
    /// for granting <paramref name="newRoleName"/> (optionally scoped to
    /// <paramref name="siteId"/>) on top of the user's existing grants.
    /// Empty list = clean. Implementations should NEVER throw for soft
    /// warnings — only HARD pairs throw <see cref="SodViolationException"/>
    /// at grant time inside the provisioning service.
    /// </summary>
    Task<IReadOnlyList<string>> GetWarningsAsync(
        Guid userId,
        string newRoleName,
        Guid? siteId,
        CancellationToken ct = default);
}

/// <summary>
/// No-op default. Returns no warnings for any combination. Registered as
/// the default <see cref="ISodService"/> so the section never crashes
/// when Phase 1's real implementation isn't wired up yet. Phase 1 will
/// override this registration with its DB-backed implementation.
/// </summary>
public sealed class NullSodService : ISodService
{
    public Task<IReadOnlyList<string>> GetWarningsAsync(
        Guid userId, string newRoleName, Guid? siteId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}

/// <summary>
/// Thrown by <c>IIdentityProvisioningService.GrantRoleAsync</c> when a
/// HARD SoD pair would be created. Not caught inside the provisioning
/// service — propagated up so the section can surface it as an error
/// banner and the parent form can refuse to commit.
/// </summary>
/// <remarks>
/// Phase 1 ships the actual throw site (the matrix of HARD pairs lives
/// in NickFinance's policy module). This file just declares the type so
/// the Phase 2 form code can <c>catch</c> it without compiling against
/// NickFinance.WebApp.
/// </remarks>
public sealed class SodViolationException : InvalidOperationException
{
    /// <summary>The role being granted that triggered the violation.</summary>
    public string RoleName { get; }

    /// <summary>The conflicting role(s) the user already holds.</summary>
    public IReadOnlyList<string> ConflictingRoles { get; }

    public SodViolationException(string roleName, IReadOnlyList<string> conflictingRoles, string message)
        : base(message)
    {
        RoleName = roleName;
        ConflictingRoles = conflictingRoles;
    }
}
