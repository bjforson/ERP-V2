using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Resolves arbitrary policy names that look like permission strings
/// (e.g. <c>"petty.voucher.approve"</c>) into one-shot
/// <see cref="AuthorizationPolicy"/> instances backed by a single
/// <see cref="PermissionRequirement"/>. Mirror of NSCIM's
/// <c>DynamicAuthorizationPolicyProvider</c>.
/// </summary>
/// <remarks>
/// <para>
/// NickFinance's Razor pages gate via
/// <c>[Authorize(Policy = Permissions.PettyVoucherApprove)]</c> — the
/// policy name IS the permission string. There's no
/// <c>"Permission:"</c> prefix because every NickFinance policy is
/// permission-shaped (the legacy fallback-policy provider handles the
/// authentication-only fallback policy at the kernel level).
/// </para>
/// <para>
/// Built policies are cached in a <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// so the second-and-subsequent request for the same policy name is
/// allocation-free.
/// </para>
/// </remarks>
public sealed class DynamicAuthorizationPolicyProvider : DefaultAuthorizationPolicyProvider
{
    private readonly ConcurrentDictionary<string, AuthorizationPolicy> _cache = new(StringComparer.Ordinal);

    public DynamicAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
        : base(options)
    {
    }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        // Built-in / explicitly registered policies (e.g. cookie + CF
        // Access combined scheme, audit-only policies, fallback policy)
        // resolve via the base class first.
        var fromBase = await base.GetPolicyAsync(policyName);
        if (fromBase is not null)
        {
            return fromBase;
        }

        // Convention: any unregistered policy name that contains a dot is
        // a permission string (e.g. "petty.voucher.approve",
        // "ar.invoice.issue", "users.manage"). Build a one-shot
        // permission-requirement policy and memoise it.
        if (LooksLikePermission(policyName))
        {
            return _cache.GetOrAdd(policyName, name =>
            {
                var builder = new AuthorizationPolicyBuilder();
                builder.AddRequirements(new PermissionRequirement(name));
                return builder.Build();
            });
        }

        return null;
    }

    /// <summary>
    /// Cheap structural check: NickFinance permission strings follow the
    /// <c>module.noun.verb</c> shape and always contain at least one dot
    /// (the home permission is <c>"home.view"</c>). Anything without a
    /// dot is treated as a non-permission policy and falls through to
    /// the default provider's null result.
    /// </summary>
    private static bool LooksLikePermission(string policyName) =>
        !string.IsNullOrWhiteSpace(policyName) && policyName.Contains('.');
}
