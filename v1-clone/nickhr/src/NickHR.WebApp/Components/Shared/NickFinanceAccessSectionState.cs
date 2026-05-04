namespace NickHR.WebApp.Components.Shared;

/// <summary>
/// Snapshot of the <c>NickFinanceAccessSection</c>'s intended state.
/// Emitted via the <c>OnStateChanged</c> event callback every time the
/// admin toggles a checkbox, picks a site, edits the phone, or changes
/// the ExternalAuditor expiry / audit-firm fields. The parent form holds
/// the latest snapshot and uses <see cref="IsValid"/> to enable / disable
/// its Save button.
/// </summary>
/// <param name="GrantAccess">
/// True if the master "Grant NickFinance access" checkbox is on. When
/// false, all role checkboxes are forced off and Apply revokes every
/// pre-existing grant.
/// </param>
/// <param name="PhoneE164">
/// The intended primary phone in E.164 format, or <see langword="null"/>
/// to clear the existing phone. Used by the WhatsApp approval notifier.
/// </param>
/// <param name="Grants">
/// One <see cref="RoleGrantIntent"/> per CHECKED role row. Site-required
/// rows that don't yet have a site picked are still listed here so the
/// form can show "you have invalid grants" — but <see cref="IsValid"/>
/// will be false in that case.
/// </param>
/// <param name="IsValid">
/// True when every checked row has its required fields populated:
/// <list type="bullet">
///   <item><description>Site* roles must have a non-null <c>SiteId</c>.</description></item>
///   <item><description><c>ExternalAuditor</c> must have both <c>ExpiresAt</c> and <c>AuditFirm</c>.</description></item>
///   <item><description>No HARD SoD violation surfaced by the most-recent grant attempt.</description></item>
/// </list>
/// </param>
public sealed record NickFinanceAccessSectionState(
    bool GrantAccess,
    string? PhoneE164,
    IReadOnlyList<RoleGrantIntent> Grants,
    bool IsValid);

/// <summary>
/// One role the admin wants the user to hold once Save is clicked.
/// Tenant-wide grants leave <see cref="SiteId"/> null.
/// </summary>
/// <param name="RoleName">Canonical role name from <see cref="NickHR.WebApp.Identity.RoleNames"/>.</param>
/// <param name="SiteId">Required for Site* roles, optional for AP/AR clerks/cashiers, must be null for HQ-only roles.</param>
/// <param name="ExpiresAt">Non-null for ExternalAuditor; ignored for every other role.</param>
/// <param name="AuditFirm">Audit firm name; non-null for ExternalAuditor only.</param>
public sealed record RoleGrantIntent(
    string RoleName,
    Guid? SiteId,
    DateTimeOffset? ExpiresAt,
    string? AuditFirm);
