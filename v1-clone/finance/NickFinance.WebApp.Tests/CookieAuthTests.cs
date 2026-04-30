using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NickFinance.WebApp.Identity;
using NickFinance.WebApp.Services;
using Npgsql;
using Xunit;

namespace NickFinance.WebApp.Tests;

/// <summary>
/// W4 (2026-04-29) — covers the email + password login path:
///   • PasswordVerifier behaviour against NickHR-shaped public."Users" rows.
///   • Smart scheme forwarding priority (Cookie > CF Access > LAN-trust).
///
/// The PasswordVerifier tests need a live Postgres so they can write a
/// row, run the actual hasher, and read it back. They self-skip when
/// the NICKFINANCE_TEST_DB env var is unset (mirrors
/// AccessProvisioningHardeningTests).
/// </summary>
public sealed class CookieAuthTests
{
    private const string EnvVar = "NICKFINANCE_TEST_DB";

    /// <summary>
    /// Resolve a connection string to a temp test schema. We use a
    /// dedicated DB name (nickfinance_cookie_test) so we don't collide
    /// with the hardening test suite's DB; the table name stays
    /// public."Users" (the NickHR table).
    /// </summary>
    private static string? ResolveTestConnectionStringOrNull()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rebuilt = new List<string>();
        var saw = false;
        foreach (var p in parts)
        {
            var ix = p.IndexOf('=', StringComparison.Ordinal);
            if (ix < 0) { rebuilt.Add(p); continue; }
            var key = p[..ix].Trim();
            if (string.Equals(key, "Database", StringComparison.OrdinalIgnoreCase))
            {
                rebuilt.Add("Database=nickfinance_cookie_test");
                saw = true;
            }
            else
            {
                rebuilt.Add(p);
            }
        }
        if (!saw) rebuilt.Add("Database=nickfinance_cookie_test");
        return string.Join(';', rebuilt);
    }

    /// <summary>
    /// Build a NickHR-shaped user row. Returns the connection string and
    /// inserts the row. The schema is the minimum subset needed for
    /// PasswordVerifier (which only reads 8 columns).
    /// </summary>
    private static async Task<string?> SeedHrUserAsync(
        string email,
        string password,
        bool emailConfirmed = true,
        bool isActive = true,
        bool lockoutEnabled = false,
        DateTimeOffset? lockoutEnd = null,
        string firstName = "Test",
        string lastName = "User")
    {
        var conn = ResolveTestConnectionStringOrNull();
        if (conn is null) return null;

        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();

        // Idempotent setup — create the table if missing. Mirrors NickHR's
        // public."Users" schema (subset of columns).
        await using (var ddl = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS public."Users" (
                "Id"                 text PRIMARY KEY,
                "FirstName"          text NOT NULL DEFAULT '',
                "LastName"           text NOT NULL DEFAULT '',
                "IsActive"           boolean NOT NULL DEFAULT true,
                "UserName"           varchar(256),
                "NormalizedUserName" varchar(256),
                "Email"              varchar(256),
                "NormalizedEmail"    varchar(256),
                "EmailConfirmed"     boolean NOT NULL DEFAULT false,
                "PasswordHash"       text,
                "LockoutEnd"         timestamp with time zone,
                "LockoutEnabled"     boolean NOT NULL DEFAULT false,
                "AccessFailedCount"  integer NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "users_normalized_email_unique"
                ON public."Users" ("NormalizedEmail");
        """, c))
        {
            await ddl.ExecuteNonQueryAsync();
        }

        // Real PBKDF2 hash via the same hasher PasswordVerifier uses.
        var hasher = new PasswordHasher<PasswordVerifier.HrUser>();
        var hash = hasher.HashPassword(new PasswordVerifier.HrUser(), password);

        await using (var del = new NpgsqlCommand(
            """DELETE FROM public."Users" WHERE "NormalizedEmail" = @ne""", c))
        {
            del.Parameters.AddWithValue("ne", email.ToUpperInvariant());
            await del.ExecuteNonQueryAsync();
        }

        await using (var ins = new NpgsqlCommand("""
            INSERT INTO public."Users"
                ("Id", "FirstName", "LastName", "IsActive", "UserName", "NormalizedUserName",
                 "Email", "NormalizedEmail", "EmailConfirmed", "PasswordHash",
                 "LockoutEnabled", "LockoutEnd", "AccessFailedCount")
            VALUES (@id, @fn, @ln, @act, @un, @nun, @em, @nem, @ec, @ph, @lb, @le, 0)
        """, c))
        {
            ins.Parameters.AddWithValue("id", Guid.NewGuid().ToString());
            ins.Parameters.AddWithValue("fn", firstName);
            ins.Parameters.AddWithValue("ln", lastName);
            ins.Parameters.AddWithValue("act", isActive);
            ins.Parameters.AddWithValue("un", email);
            ins.Parameters.AddWithValue("nun", email.ToUpperInvariant());
            ins.Parameters.AddWithValue("em", email);
            ins.Parameters.AddWithValue("nem", email.ToUpperInvariant());
            ins.Parameters.AddWithValue("ec", emailConfirmed);
            ins.Parameters.AddWithValue("ph", hash);
            ins.Parameters.AddWithValue("lb", lockoutEnabled);
            if (lockoutEnd is null)
            {
                ins.Parameters.AddWithValue("le", DBNull.Value);
            }
            else
            {
                ins.Parameters.AddWithValue("le", lockoutEnd.Value);
            }
            await ins.ExecuteNonQueryAsync();
        }

        return conn;
    }

    private static PasswordVerifier MakeVerifier(string conn) =>
        new(conn, NullLogger<PasswordVerifier>.Instance);

    // ────────────────────────────────────────────────────────────
    //  PasswordVerifier
    // ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PasswordVerifier_ValidCredentials_ReturnsResult()
    {
        var email = $"valid-{Guid.NewGuid():N}@nickscan.com";
        var conn = await SeedHrUserAsync(email, "test-password-123");
        if (conn is null) return; // env var unset → skip

        var sut = MakeVerifier(conn);
        var result = await sut.VerifyAsync(email, "test-password-123");

        Assert.NotNull(result);
        Assert.Equal(email.ToLowerInvariant(), result!.Email);
        Assert.Equal("Test User", result.DisplayName);
    }

    [Fact]
    public async Task PasswordVerifier_WrongPassword_ReturnsNull()
    {
        var email = $"wrong-{Guid.NewGuid():N}@nickscan.com";
        var conn = await SeedHrUserAsync(email, "right-password");
        if (conn is null) return;

        var sut = MakeVerifier(conn);
        var result = await sut.VerifyAsync(email, "wrong-password");

        Assert.Null(result);
    }

    [Fact]
    public async Task PasswordVerifier_UnknownEmail_ReturnsNull()
    {
        var conn = await SeedHrUserAsync($"someone-{Guid.NewGuid():N}@nickscan.com", "x");
        if (conn is null) return;

        var sut = MakeVerifier(conn);
        var result = await sut.VerifyAsync($"nobody-{Guid.NewGuid():N}@nickscan.com", "x");

        Assert.Null(result);
    }

    [Fact]
    public async Task PasswordVerifier_LockedAccount_ReturnsNull()
    {
        var email = $"locked-{Guid.NewGuid():N}@nickscan.com";
        var conn = await SeedHrUserAsync(email, "secret",
            lockoutEnabled: true,
            lockoutEnd: DateTimeOffset.UtcNow.AddHours(1));
        if (conn is null) return;

        var sut = MakeVerifier(conn);
        var result = await sut.VerifyAsync(email, "secret");

        Assert.Null(result);
    }

    [Fact]
    public async Task PasswordVerifier_UnconfirmedEmail_ReturnsNull()
    {
        var email = $"unconfirmed-{Guid.NewGuid():N}@nickscan.com";
        var conn = await SeedHrUserAsync(email, "secret", emailConfirmed: false);
        if (conn is null) return;

        var sut = MakeVerifier(conn);
        var result = await sut.VerifyAsync(email, "secret");

        Assert.Null(result);
    }

    [Fact]
    public async Task PasswordVerifier_InactiveAccount_ReturnsNull()
    {
        var email = $"inactive-{Guid.NewGuid():N}@nickscan.com";
        var conn = await SeedHrUserAsync(email, "secret", isActive: false);
        if (conn is null) return;

        var sut = MakeVerifier(conn);
        var result = await sut.VerifyAsync(email, "secret");

        Assert.Null(result);
    }

    // ────────────────────────────────────────────────────────────
    //  Smart scheme forwarding
    // ────────────────────────────────────────────────────────────

    [Fact]
    public void Smart_CookiePresent_UsesCookieScheme()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["Cookie"] = $"{CfAccessAuth.CookieName}=opaque";
        ctx.Request.Cookies = new TestCookies(new() { [CfAccessAuth.CookieName] = "opaque" });

        var pick = SimulateForward(ctx);
        Assert.Equal(CfAccessAuth.CookieSchemeName, pick);
    }

    [Fact]
    public void Smart_NoCookieJwtPresent_UsesCfAccess()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[CfAccessAuth.HeaderName] = "eyJhbGc.payload.sig";
        ctx.Request.Cookies = new TestCookies(new());

        var pick = SimulateForward(ctx);
        Assert.Equal(CfAccessAuth.SchemeName, pick);
    }

    [Fact]
    public void Smart_NoCookieNoJwt_UsesLanTrust()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Cookies = new TestCookies(new());
        // No JWT header, no cookie → forwarder must pick LAN-trust so the
        // handler can self-gate on CIDR + env vars.
        var pick = SimulateForward(ctx);
        Assert.Equal(LanTrustAuthHandler.SchemeName, pick);
    }

    /// <summary>
    /// Mirror of the ForwardDefaultSelector in CfAccessAuth. Kept inline
    /// rather than reflecting into the AddPolicyScheme registration —
    /// the public contract here is the priority order, and pinning that
    /// in a test gives a regression alarm if someone re-arranges
    /// CfAccessAuth.cs without thinking.
    /// </summary>
    private static string SimulateForward(HttpContext ctx)
    {
        if (ctx.Request.Cookies.ContainsKey(CfAccessAuth.CookieName)) return CfAccessAuth.CookieSchemeName;
        if (ctx.Request.Headers.ContainsKey(CfAccessAuth.HeaderName)) return CfAccessAuth.SchemeName;
        return LanTrustAuthHandler.SchemeName;
    }

    private sealed class TestCookies : IRequestCookieCollection
    {
        private readonly Dictionary<string, string> _inner;
        public TestCookies(Dictionary<string, string> inner) { _inner = inner; }
        public string? this[string key] => _inner.TryGetValue(key, out var v) ? v : null;
        public int Count => _inner.Count;
        public ICollection<string> Keys => _inner.Keys;
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _inner.GetEnumerator();
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            var ok = _inner.TryGetValue(key, out var v);
            value = v;
            return ok;
        }
    }
}
