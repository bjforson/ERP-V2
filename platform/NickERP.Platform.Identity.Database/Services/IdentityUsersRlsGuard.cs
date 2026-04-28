using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace NickERP.Platform.Identity.Database.Services;

/// <summary>
/// Sprint 2 — H2 Identity-Tenancy Interlock guard. Confirms at host startup
/// that <c>identity.identity_users</c> is NOT under
/// <c>FORCE ROW LEVEL SECURITY</c>. This is the carve-out installed by
/// migration <c>20260428104421_RemoveRlsFromIdentityUsers</c>: the table
/// sits at the root of the auth flow — <see cref="DbIdentityResolver"/>
/// must read it before <c>app.tenant_id</c> can be set, so RLS on it is
/// fundamentally circular under non-superuser DB roles (<c>nscim_app</c>).
///
/// <para>
/// If the check finds FORCE RLS re-enabled (e.g. a future migration
/// silently flips it back on), this logs a structured
/// <c>IDENTITY-USERS-RLS-RE-ENABLED</c> warning. It deliberately does NOT
/// throw — too aggressive in dev when migrations might be mid-apply, and a
/// thrown exception would block boot for a problem a single follow-up
/// migration can fix.
/// </para>
///
/// <para>
/// Call <see cref="EnsureCarveOutAsync"/> once during host startup, after
/// migrations have been applied (see <c>NickERP.Inspection.Web/Program.cs</c>).
/// It opens a single short-lived connection.
/// </para>
/// </summary>
public static class IdentityUsersRlsGuard
{
    /// <summary>
    /// Runs the one-shot pg_class probe and emits a structured warning if
    /// <c>identity.identity_users</c> is under <c>FORCE ROW LEVEL SECURITY</c>.
    /// Never throws — failures are caught and logged at <see cref="LogLevel.Warning"/>.
    /// </summary>
    public static async Task EnsureCarveOutAsync(
        IdentityDbContext db,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            var conn = db.Database.GetDbConnection();
            var openedHere = false;
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
                openedHere = true;
            }

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT relforcerowsecurity FROM pg_class "
                    + "WHERE relname = 'identity_users' "
                    + "AND relnamespace = 'identity'::regnamespace;";

                var raw = await cmd.ExecuteScalarAsync(ct);
                if (raw is bool forceRls && forceRls)
                {
                    logger.LogWarning(
                        "IDENTITY-USERS-RLS-RE-ENABLED: identity.identity_users has FORCE ROW LEVEL "
                        + "SECURITY enabled. Auth flow will fail under non-superuser DB roles. "
                        + "Re-apply the H2 carve-out migration "
                        + "(20260428104421_RemoveRlsFromIdentityUsers).");
                }
            }
            finally
            {
                if (openedHere)
                {
                    await conn.CloseAsync();
                }
            }
        }
        catch (Exception ex)
        {
            // Never fail startup over the guard itself.
            logger.LogWarning(
                ex,
                "Sprint H2 carve-out check could not run against identity.identity_users.");
        }
    }
}
