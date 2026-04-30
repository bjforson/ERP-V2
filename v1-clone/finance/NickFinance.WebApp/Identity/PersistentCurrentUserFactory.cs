using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Identity;
using NickFinance.WebApp.Services;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// W3B (2026-04-29) — provisioned-only lookup. Before W3B this factory
/// auto-created a <see cref="User"/> row on first CF Access login. After
/// W3B:
/// <list type="bullet">
///   <item><description><b>Production + CF Access on:</b> lookup-only. If
///     no row exists for the CF Access sub or email, throws
///     <see cref="AccessNotProvisionedException"/> — Program.cs's exception
///     handler short-circuits the request to <c>/access-not-provisioned</c>.</description></item>
///   <item><description><b>Non-Production / dev fallback:</b> keep lazy
///     creation so <c>dotnet run</c> still works without HR-side seeding.</description></item>
/// </list>
/// HR (NickHR.WebApp) is now the system-of-record: every employee or
/// admin user is created in <c>identity.users</c> by HR's
/// <c>IIdentityProvisioningService</c>, and NickFinance never inserts.
/// </summary>
/// <remarks>
/// Backwards-compatible <c>Resolve</c> overloads (without
/// <see cref="IHostEnvironment"/>) keep older test fixtures and any
/// non-WebApp consumers working.
/// </remarks>
public static class PersistentCurrentUserFactory
{
    /// <summary>The fixed dev/fallback user UUID. Stable across runs.</summary>
    public static readonly Guid DevUserId = new("00000000-0000-0000-0000-000000000001");

    /// <summary>
    /// Resolve with environment awareness. Production CF Access path is
    /// lookup-only (throws <see cref="AccessNotProvisionedException"/>);
    /// the dev path lazy-creates as before.
    /// </summary>
    public static CurrentUser Resolve(
        IServiceProvider sp,
        IConfiguration config,
        bool cfAccessOn,
        long defaultTenantId,
        IHostEnvironment env)
    {
        var ctx = sp.GetService<IHttpContextAccessor>()?.HttpContext;
        ClaimsPrincipal? principal = null;

        if (cfAccessOn)
        {
            principal = ctx?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                // Circuit alive but HTTP context rolled off — read from auth state provider.
                var asp = sp.GetService<AuthenticationStateProvider>();
                if (asp is not null)
                {
                    var state = asp.GetAuthenticationStateAsync().GetAwaiter().GetResult();
                    if (state.User.Identity?.IsAuthenticated == true)
                    {
                        principal = state.User;
                    }
                }
            }
        }

        var db = sp.GetRequiredService<IdentityDbContext>();
        var isProduction = env.IsProduction();

        CurrentUser cu;
        if (cfAccessOn && principal?.Identity?.IsAuthenticated == true)
        {
            var sub = principal.FindFirst("sub")?.Value
                   ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var email = (principal.FindFirst("email")?.Value
                      ?? principal.FindFirst(ClaimTypes.Email)?.Value
                      ?? "unknown@nickscan.com").ToLowerInvariant();
            var displayName = principal.FindFirst("name")?.Value
                           ?? principal.FindFirst(ClaimTypes.Name)?.Value
                           ?? PrettyNameFromEmail(email);

            // W4 (2026-04-29) — if the principal came in via the cookie
            // login path, the "sub" claim is the internal_user_id GUID
            // (we put it there at SignInAsync time). It is NOT a CF
            // Access sub. Match by InternalUserId first; if the row is
            // missing (admin disabled them mid-session, race with HR),
            // throw AccessNotProvisionedException — Program.cs's
            // middleware turns that into the friendly /access-not-provisioned
            // page, the cookie's still alive but the user gets a clear
            // message to re-auth. Distinguishing happens via the
            // "nickerp.via" claim, which the LanTrustAuthHandler stamps
            // and the cookie-login flow also stamps as "cookie".
            var via = principal.FindFirst("nickerp.via")?.Value;
            if (string.Equals(via, "cookie", StringComparison.Ordinal))
            {
                cu = LoadCookieUser(db, sub, email, defaultTenantId);
            }
            else
            {
                // Production: lookup-only iff hardening is explicitly opt-in.
                // Default (env var unset) keeps the pre-W4 lazy-create behaviour
                // so a fresh deploy doesn't lock everyone out before NickHR has
                // pre-provisioned users. Once NickHR is live and the identity.users
                // population is steady-state, set NICKFINANCE_REQUIRE_PROVISIONED_USER=true
                // to flip on the strict path. Dev/Staging: always lazy-create
                // (preserves local `dotnet run` ergonomics).
                var requireProvisioned = isProduction
                    && string.Equals(
                        Environment.GetEnvironmentVariable("NICKFINANCE_REQUIRE_PROVISIONED_USER"),
                        "true",
                        StringComparison.OrdinalIgnoreCase);
                cu = requireProvisioned
                    ? LoadOrThrow(db, sub, email, defaultTenantId)
                    : LoadOrCreate(db, sub, email, displayName, defaultTenantId);
            }
        }
        else
        {
            // Dev / fallback path — make sure the row exists so audit FKs hold.
            var section = config.GetSection("NickFinance:DevUser");
            var displayName = section["DisplayName"] ?? "Local Dev";
            var email = (section["Email"] ?? "dev@nickscan.com").ToLowerInvariant();
            cu = LoadOrCreateById(db, DevUserId, email, displayName, defaultTenantId);
        }

        // Hand the tenant id to the tenant accessor so subsequent DbContext queries scope.
        if (ctx is not null)
        {
            ctx.Items[HttpContextTenantAccessor.ItemsKey] = cu.TenantId;
        }
        return cu;
    }

    /// <summary>
    /// Backwards-compatible resolve (pre-W3B). Calls into the env-aware
    /// overload with a Development host so existing test fixtures retain
    /// their lazy-create behaviour.
    /// </summary>
    public static CurrentUser Resolve(
        IServiceProvider sp,
        IConfiguration config,
        bool cfAccessOn,
        long defaultTenantId)
        => Resolve(sp, config, cfAccessOn, defaultTenantId, DevelopmentEnv.Instance);

    /// <summary>
    /// W3B production path — never inserts. Looks up by cf_access_sub
    /// first (stable across email rotations), falls back to email + tenant.
    /// Throws <see cref="AccessNotProvisionedException"/> if neither
    /// matches — Program.cs's exception handler turns that into the
    /// /access-not-provisioned page.
    /// </summary>
    private static CurrentUser LoadOrThrow(IdentityDbContext db, string? cfAccessSub, string email, long tenantId)
    {
        User? row = null;
        if (!string.IsNullOrEmpty(cfAccessSub))
        {
            row = db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.CfAccessSub == cfAccessSub);
        }
        row ??= db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.Email == email && u.TenantId == tenantId);

        if (row is null)
        {
            throw new AccessNotProvisionedException(email, cfAccessSub);
        }

        // Backfill cf_access_sub on first sub-equipped login.
        var changed = false;
        if (!string.IsNullOrEmpty(cfAccessSub) && string.IsNullOrEmpty(row.CfAccessSub))
        {
            row.CfAccessSub = cfAccessSub;
            changed = true;
        }
        var now = DateTimeOffset.UtcNow;
        if (row.LastSeenAt is null || row.LastSeenAt < now.AddMinutes(-1))
        {
            row.LastSeenAt = now;
            changed = true;
        }
        if (changed)
        {
            try { db.SaveChanges(); } catch { /* race-safe; the next request will retry */ }
        }
        return new CurrentUser(row.InternalUserId, row.DisplayName, row.Email, row.TenantId);
    }

    /// <summary>
    /// W4 (2026-04-29) — resolve the <see cref="CurrentUser"/> for a
    /// principal that came in via the cookie-login path. The "sub" claim
    /// here is the canonical <c>identity.users.internal_user_id</c> GUID
    /// (stamped at SignInAsync), so we match on that first. We never
    /// lazy-create on the cookie path — the AuthEndpoints' login handler
    /// has already verified that an identity row exists; if it doesn't
    /// here, the row was deleted between login and now. Throwing
    /// AccessNotProvisionedException routes the request to the friendly
    /// page; the user can re-login (which will fail provisioning) or
    /// log out manually.
    /// </summary>
    private static CurrentUser LoadCookieUser(IdentityDbContext db, string? subClaim, string email, long tenantId)
    {
        User? row = null;
        if (Guid.TryParse(subClaim, out var internalUserId))
        {
            row = db.Users.IgnoreQueryFilters()
                .FirstOrDefault(u => u.InternalUserId == internalUserId);
        }
        // Fallback: in case the cookie was minted before we stamped sub
        // as a GUID, or the GUID parse failed for any reason.
        row ??= db.Users.IgnoreQueryFilters()
            .FirstOrDefault(u => u.Email == email && u.TenantId == tenantId);

        if (row is null || row.Status != UserStatus.Active)
        {
            // Disabled-mid-session or row vanished. Surface the same
            // exception CF Access does so Program.cs's middleware can
            // bounce to /access-not-provisioned.
            throw new AccessNotProvisionedException(email, subClaim);
        }

        // Touch last-seen so the audit trail reflects activity. Race-safe.
        var now = DateTimeOffset.UtcNow;
        if (row.LastSeenAt is null || row.LastSeenAt < now.AddMinutes(-1))
        {
            row.LastSeenAt = now;
            try { db.SaveChanges(); } catch { /* race-safe */ }
        }
        return new CurrentUser(row.InternalUserId, row.DisplayName, row.Email, row.TenantId);
    }

    private static CurrentUser LoadOrCreate(IdentityDbContext db, string? cfAccessSub, string email, string displayName, long tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        // Match by cf_access_sub first (most stable); fall back to email
        // (handles the migration gap where existing accounts predate the sub).
        User? row = null;
        if (!string.IsNullOrEmpty(cfAccessSub))
        {
            row = db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.CfAccessSub == cfAccessSub);
        }
        row ??= db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.Email == email && u.TenantId == tenantId);

        if (row is null)
        {
            row = new User
            {
                CfAccessSub = cfAccessSub,
                Email = email,
                DisplayName = displayName,
                Status = UserStatus.Active,
                CreatedAt = now,
                LastSeenAt = now,
                TenantId = tenantId,
            };
            db.Users.Add(row);
            try
            {
                db.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // Race: another circuit just inserted the same sub. Reload.
                db.Entry(row).State = EntityState.Detached;
                row = db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.CfAccessSub == cfAccessSub)
                   ?? db.Users.IgnoreQueryFilters().First(u => u.Email == email && u.TenantId == tenantId);
            }
        }
        else
        {
            // Backfill cf_access_sub on first sub-equipped login if it was email-keyed before.
            if (!string.IsNullOrEmpty(cfAccessSub) && string.IsNullOrEmpty(row.CfAccessSub))
            {
                row.CfAccessSub = cfAccessSub;
            }
            row.LastSeenAt = now;
            try { db.SaveChanges(); } catch { /* race-safe; ignore */ }
        }

        return new CurrentUser(row.InternalUserId, row.DisplayName, row.Email, row.TenantId);
    }

    private static CurrentUser LoadOrCreateById(IdentityDbContext db, Guid id, string email, string displayName, long tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var row = db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.InternalUserId == id);
        if (row is null)
        {
            row = new User
            {
                InternalUserId = id,
                CfAccessSub = null,
                Email = email,
                DisplayName = displayName,
                Status = UserStatus.Active,
                CreatedAt = now,
                LastSeenAt = now,
                TenantId = tenantId,
            };
            db.Users.Add(row);
            try { db.SaveChanges(); } catch { /* race-safe */ }
        }
        else
        {
            row.LastSeenAt = now;
            try { db.SaveChanges(); } catch { /* race-safe */ }
        }
        return new CurrentUser(row.InternalUserId, row.DisplayName, row.Email, row.TenantId);
    }

    private static string PrettyNameFromEmail(string email)
    {
        var local = email.Split('@', 2)[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return email;
        return string.Join(' ', parts.Select(p => p.Length switch
        {
            0 => p,
            1 => char.ToUpperInvariant(p[0]).ToString(),
            _ => char.ToUpperInvariant(p[0]) + p[1..]
        }));
    }

    /// <summary>
    /// Sentinel <see cref="IHostEnvironment"/> used by the
    /// backwards-compatible <see cref="Resolve(IServiceProvider, IConfiguration, bool, long)"/>
    /// overload. Always reports Development so legacy callers retain
    /// the lazy-create behaviour.
    /// </summary>
    private sealed class DevelopmentEnv : IHostEnvironment
    {
        public static readonly DevelopmentEnv Instance = new();
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NickFinance.WebApp";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

/// <summary>
/// Thrown by <see cref="PersistentCurrentUserFactory"/> in production
/// when a CF-Access-authenticated user has no row in
/// <c>identity.users</c>. The middleware in <c>Program.cs</c> catches
/// this and short-circuits the request to the
/// <c>/access-not-provisioned</c> page.
/// </summary>
public sealed class AccessNotProvisionedException : Exception
{
    /// <summary>Lower-cased email pulled from the CF Access claims.</summary>
    public string Email { get; }

    /// <summary>CF Access <c>sub</c> claim, if any.</summary>
    public string? CfAccessSub { get; }

    public AccessNotProvisionedException(string email, string? cfAccessSub)
        : base($"No identity.users row exists for {email} (sub={cfAccessSub ?? "<none>"}). Ask HR to provision access.")
    {
        Email = email;
        CfAccessSub = cfAccessSub;
    }
}
