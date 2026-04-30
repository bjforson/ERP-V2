using System.Data;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace NickFinance.WebApp.Identity;

/// <summary>
/// Reads NickHR's ASP.NET Identity password store ('public."Users"' in the
/// shared 'nickhr' Postgres database) and verifies a plaintext password
/// against the stored PBKDF2 hash. NickFinance does NOT write to this
/// table — password reset, account lockout state, and email confirmation
/// remain owned by NickHR (https://hr.nickscan.net). This service exists
/// so that LAN-direct NickFinance users can authenticate as themselves
/// (per-user audit) using the credentials they already have for NickHR.
///
/// <para>Discovery (verified 2026-04-29 against the live nickhr DB):</para>
/// <list type="bullet">
///   <item><description>NickHR remaps the default ASP.NET Identity table
///   <c>AspNetUsers</c> → <c>public."Users"</c> in
///   <see cref="NickHR.Infrastructure.Data.NickHRDbContext.OnModelCreating"/>
///   (line 175). Column names are the standard <see cref="IdentityUser"/>
///   set: Id, UserName, NormalizedUserName, Email, NormalizedEmail,
///   PasswordHash, EmailConfirmed, LockoutEnd, LockoutEnabled,
///   AccessFailedCount, plus NickHR's IsActive (boolean) extension.</description></item>
///   <item><description>The password hash is produced by ASP.NET Identity's
///   <see cref="PasswordHasher{TUser}"/> (default v3 PBKDF2 / 10000 iters,
///   HMAC-SHA256) — NickHR has no custom hasher registered.</description></item>
///   <item><description>Connection string env var is shared:
///   <c>ConnectionStrings:Finance</c> (NickFinance) and NickHR's
///   <c>NICKHR_DB_PASSWORD</c> both target <c>nickhr</c> on the same host.
///   The cookie-login path connects with <c>nscim_app</c>, the same
///   non-superuser role NickFinance already uses. Tenant RLS still applies
///   — the connection sets <c>app.tenant_id</c> via the existing
///   TenantConnectionInterceptor, but our parameterised SELECT against
///   <c>public."Users"</c> is in the <c>public</c> schema (no RLS policies
///   attached there, verified via \d).</description></item>
/// </list>
///
/// <para>Caller responsibility: after VerifyAsync returns a non-null result,
/// the caller must look up the matching <c>identity.users</c> row by email
/// (HR-provisioned). Failure to find one = "your password is correct, but
/// your NickFinance access hasn't been provisioned" — distinct from
/// invalid-credentials.</para>
/// </summary>
public interface IPasswordVerifier
{
    /// <summary>
    /// Look up the user in NickHR's <c>public."Users"</c> table by lower-cased
    /// email, verify the password against <c>PasswordHash</c> using ASP.NET
    /// Identity's <see cref="PasswordHasher{TUser}"/>, and return the verified
    /// email + display name on success. Returns null for any of:
    /// <list type="bullet">
    ///   <item>Unknown email</item>
    ///   <item>Password mismatch</item>
    ///   <item>Account locked out (LockoutEnabled=true and LockoutEnd in the future)</item>
    ///   <item>Email not confirmed</item>
    ///   <item>NickHR's IsActive=false</item>
    /// </list>
    /// The login UI surfaces these uniformly as "invalid credentials" — never
    /// distinguish, never enumerate accounts.
    /// </summary>
    Task<PasswordVerifyResult?> VerifyAsync(string email, string password, CancellationToken ct = default);
}

/// <param name="Email">Lower-cased email pulled from the row.</param>
/// <param name="DisplayName">"FirstName LastName" if both populated; falls back to email-prefix prettified.</param>
public sealed record PasswordVerifyResult(string Email, string? DisplayName);

/// <summary>
/// Default <see cref="IPasswordVerifier"/>. Constructed with the same
/// connection string NickFinance uses for its EF DbContexts; opens a
/// fresh Npgsql connection per verify so we don't tangle with the
/// per-request EF interceptor that pushes <c>app.tenant_id</c>. The
/// SELECT is parameterised, scoped to <c>public."Users"</c>, and reads
/// only the columns needed for verification.
/// </summary>
public sealed class PasswordVerifier : IPasswordVerifier
{
    private readonly string _connectionString;
    private readonly ILogger<PasswordVerifier> _log;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Static <see cref="PasswordHasher{TUser}"/>. The hasher itself is
    /// stateless once constructed (reads no per-call options), so a single
    /// instance is safe across concurrent requests.
    /// </summary>
    private static readonly PasswordHasher<HrUser> Hasher = new();

    public PasswordVerifier(string connectionString, ILogger<PasswordVerifier> log, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<PasswordVerifyResult?> VerifyAsync(string email, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        // ASP.NET Identity normalises emails by upper-casing for the
        // NormalizedEmail index. We mirror that here so the index is hit.
        var normalized = email.Trim().ToUpperInvariant();

        await using var conn = new NpgsqlConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PasswordVerifier failed to open DB connection.");
            return null;
        }

        // Parameterised — no string interpolation into SQL. The quoted
        // table/column names are required because NickHR keeps the
        // PascalCase identifiers from default Identity, and unquoted
        // identifiers Postgres folds to lowercase (would 42703 here).
        const string sql = """
            SELECT "Email", "PasswordHash", "EmailConfirmed", "LockoutEnabled", "LockoutEnd",
                   "IsActive", "FirstName", "LastName"
            FROM public."Users"
            WHERE "NormalizedEmail" = @ne
            LIMIT 1
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ne", NpgsqlTypes.NpgsqlDbType.Varchar, normalized);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Unknown email. Don't log the email at info level (audit /
            // forensic logs in the caller record the attempt).
            _log.LogDebug("PasswordVerifier: no user row for normalised email.");
            return null;
        }

        var dbEmail = reader.IsDBNull(0) ? null : reader.GetString(0);
        var hash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var emailConfirmed = !reader.IsDBNull(2) && reader.GetBoolean(2);
        var lockoutEnabled = !reader.IsDBNull(3) && reader.GetBoolean(3);
        var lockoutEnd = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(4);
        var isActive = !reader.IsDBNull(5) && reader.GetBoolean(5);
        var firstName = reader.IsDBNull(6) ? null : reader.GetString(6);
        var lastName = reader.IsDBNull(7) ? null : reader.GetString(7);

        // Pre-checks: account state. Done BEFORE hash verification so we
        // don't waste CPU on a PBKDF2 verify for accounts that are blocked
        // anyway. The login UI flattens all these to "invalid credentials"
        // — we never tell the caller which check failed.
        if (string.IsNullOrEmpty(hash))
        {
            _log.LogDebug("PasswordVerifier: row has no PasswordHash (NickHR row predates password set).");
            return null;
        }
        if (!emailConfirmed)
        {
            _log.LogDebug("PasswordVerifier: row has EmailConfirmed=false.");
            return null;
        }
        if (!isActive)
        {
            _log.LogDebug("PasswordVerifier: row has IsActive=false.");
            return null;
        }
        if (lockoutEnabled && lockoutEnd is { } end && end > _clock.GetUtcNow())
        {
            _log.LogDebug("PasswordVerifier: lockout active until {End:o}.", end);
            return null;
        }

        // PasswordHasher is keyed on TUser only via reference identity for
        // rehash decisions; we pass a throw-away HrUser. SuccessRehashNeeded
        // is treated as success — we don't rehash here (that's NickHR's
        // job; updating the hash from a foreign service would race with
        // HR's UserManager and invalidate ConcurrencyStamp).
        var verify = Hasher.VerifyHashedPassword(new HrUser(), hash, password);
        if (verify == PasswordVerificationResult.Failed)
        {
            return null;
        }

        var display = !string.IsNullOrWhiteSpace(firstName) || !string.IsNullOrWhiteSpace(lastName)
            ? $"{firstName} {lastName}".Trim()
            : null;

        return new PasswordVerifyResult(
            Email: (dbEmail ?? email).ToLowerInvariant(),
            DisplayName: display);
    }

    /// <summary>
    /// Marker type for <see cref="PasswordHasher{TUser}"/>. The hasher's
    /// generic parameter is only used for nullability; no per-row state
    /// is needed.
    /// </summary>
    internal sealed class HrUser { }
}
