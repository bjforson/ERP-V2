using Microsoft.AspNetCore.Http;
using NickERP.Platform.Identity;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Real <see cref="ITenantAccessor"/> for the live WebApp. Reads the
/// current tenant id from the authenticated <see cref="CurrentUser"/>
/// stored on <see cref="HttpContext.Items"/> by the
/// <see cref="PersistentCurrentUserFactory"/> so we never hit the DB
/// twice in one request.
/// </summary>
/// <remarks>
/// Trade-off: the factory has to populate <c>HttpContext.Items["tenant_id"]</c>
/// after it loads the user. Until then this returns <c>null</c>, which means
/// the very first DB call inside the factory itself sees every row — that's
/// safe because the factory's lookup is by <c>cf_access_sub</c>, a globally
/// unique key.
/// </remarks>
public sealed class HttpContextTenantAccessor : ITenantAccessor
{
    public const string ItemsKey = "nf-tenant-id";

    private readonly IHttpContextAccessor _http;

    public HttpContextTenantAccessor(IHttpContextAccessor http)
    {
        _http = http;
    }

    public long? Current
    {
        get
        {
            var ctx = _http.HttpContext;
            if (ctx is null) return null;
            if (ctx.Items.TryGetValue(ItemsKey, out var raw) && raw is long t) return t;
            return null;
        }
    }
}
