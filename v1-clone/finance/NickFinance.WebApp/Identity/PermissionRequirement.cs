using Microsoft.AspNetCore.Authorization;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Authorization requirement that carries a single permission name (e.g.
/// <c>"petty.voucher.approve"</c>). The <see cref="PermissionAuthorizationHandler"/>
/// resolves the requirement against the user's bundle from
/// <see cref="IPermissionService"/>.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public string PermissionName { get; }

    public PermissionRequirement(string permissionName)
    {
        if (string.IsNullOrWhiteSpace(permissionName))
        {
            throw new ArgumentException("Permission name is required.", nameof(permissionName));
        }
        PermissionName = permissionName;
    }
}
