using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Tenancy;
using Npgsql;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 9 / FU-userid — verifies the new
/// <c>tenant_user_isolation_notifications</c> RLS policy on
/// <c>audit.notifications</c> against a real Postgres cluster under the
/// production-equivalent <c>nscim_app</c> role.
///
/// <para>
/// Two behaviours are exercised:
/// </para>
/// <list type="bullet">
///   <item><description>A notification belonging to user A in tenant T is
///         NOT visible to user B in tenant T (the policy filters on both
///         tenant + user — promotion of the previous LINQ-only filter to
///         the DB layer).</description></item>
///   <item><description>System context (mirroring the projector's insert
///         path) can INSERT a notification whose <c>UserId</c> doesn't
///         match the session's <c>app.user_id</c>; the OR clause on
///         <c>app.tenant_id = '-1'</c> admits the write.</description></item>
/// </list>
///
/// <para>
/// Skipped silently when <c>NICKSCAN_DB_PASSWORD</c> is not set so CI
/// without dev Postgres doesn't choke. Mirrors the convention used by
/// <see cref="SystemContextTests"/>.
/// </para>
/// </summary>
public sealed class NotificationsRlsIsolationTests : IDisposable
{
    private const string PlatformDb = "nickerp_platform";
    private const long TestTenantId = 1L;

    private readonly string? _password;
    private readonly List<Guid> _seededEventIds = new();
    private readonly List<Guid> _seededNotificationIds = new();

    public NotificationsRlsIsolationTests()
    {
        _password = Environment.GetEnvironmentVariable("NICKSCAN_DB_PASSWORD");
    }

    /// <summary>
    /// Two notifications in tenant 1 — one for Alice, one for Bob. Under
    /// Alice's user context, only Alice's notification should be visible.
    /// The previous Sprint-8 P3 policy was tenant-only and would have
    /// returned both rows; FU-userid's combined policy filters on UserId
    /// too, so this test would fail before the migration applies and
    /// passes after.
    /// </summary>
    [Fact]
    public async Task UserIsolation_HidesOtherUsersNotifications_InSameTenant()
    {
        if (string.IsNullOrEmpty(_password)) return;

        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        // Seed under postgres (BYPASSRLS) so we control the rows directly.
        // Each notification needs a real audit.events FK target — the seeder
        // creates one per call when existingEventId is null.
        var aliceNotifId = await SeedNotificationAsync(alice, TestTenantId);
        var bobNotifId = await SeedNotificationAsync(bob, TestTenantId);

        // Read as Alice through the regular tenant + user context.
        await using var ctx = BuildAuditDbContext(PlatformDb, out var tenant, out var user);
        tenant.SetTenant(TestTenantId);
        user.SetUser(alice);

        var visible = await ctx.Notifications
            .AsNoTracking()
            .ToListAsync();

        visible.Should().NotContain(n => n.Id == bobNotifId,
            "the tenant_user_isolation_notifications RLS policy must hide Bob's row from Alice");
        visible.Should().Contain(n => n.Id == aliceNotifId,
            "Alice still sees her own row through the same combined policy");
    }

    /// <summary>
    /// SetSystemContext() lets <c>nscim_app</c> insert a notification
    /// whose <c>UserId</c> doesn't match the session's <c>app.user_id</c>.
    /// This is the mode the <c>AuditNotificationProjector</c> runs in:
    /// no current user (background worker), so <c>app.user_id</c>
    /// resolves to the zero UUID, but the row carries a real human's
    /// <c>UserId</c>.
    /// </summary>
    [Fact]
    public async Task SystemContext_PermitsInsertOfNotificationForOtherUser()
    {
        if (string.IsNullOrEmpty(_password)) return;

        // Seed an audit.events row first because audit.notifications has a
        // FK to events.EventId. Use the existing audit.events
        // system-context opt-in (Sprint 5) to insert a NULL-tenant or
        // tenant-1 row under the same elevated context — simpler to do
        // it via postgres.
        var eventId = await SeedAuditEventAsync(TestTenantId);
        var targetUser = Guid.NewGuid();

        await using var ctx = BuildAuditDbContext(PlatformDb, out var tenant, out _);
        tenant.SetSystemContext();

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = TestTenantId,
            UserId = targetUser,
            EventId = eventId,
            EventType = "fu-userid.test.event",
            Title = "system insert smoke",
            Body = "FU-userid Phase B smoke",
            Link = "/cases/" + Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        ctx.Notifications.Add(notification);
        await ctx.SaveChangesAsync();
        _seededNotificationIds.Add(notification.Id);

        // Verify under postgres that the row landed.
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString(PlatformDb));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM audit.notifications WHERE \"Id\" = @id;";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = notification.Id;
        cmd.Parameters.Add(p);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1L,
            "the system-context OR clause on tenant_user_isolation_notifications "
            + "must admit cross-user inserts when the session is in system mode");
    }

    private async Task<Guid> SeedAuditEventAsync(long tenantId)
    {
        var eventId = Guid.NewGuid();
        var idempotencyKey = $"fu-userid-rls-{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString(PlatformDb));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO audit.events (\"EventId\", \"OccurredAt\", \"IngestedAt\", "
            + "\"EventType\", \"EntityType\", \"EntityId\", \"TenantId\", "
            + "\"IdempotencyKey\", \"Payload\") "
            + "VALUES (@eid, now(), now(), 'fu-userid.test.event', "
            + "'NotificationsRlsIsolationTests', 'seed', @tid, @key, '{}'::jsonb);";
        cmd.Parameters.Add(new NpgsqlParameter("eid", eventId));
        cmd.Parameters.Add(new NpgsqlParameter("tid", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("key", idempotencyKey));
        await cmd.ExecuteNonQueryAsync();
        _seededEventIds.Add(eventId);
        return eventId;
    }

    private async Task<Guid> SeedNotificationAsync(Guid userId, long tenantId, Guid? existingEventId = null)
    {
        var eventId = existingEventId ?? await SeedAuditEventAsync(tenantId);
        var notifId = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(BuildAdminConnectionString(PlatformDb));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO audit.notifications "
            + "(\"Id\", \"TenantId\", \"UserId\", \"EventId\", \"EventType\", \"Title\", \"Body\", \"Link\", \"CreatedAt\") "
            + "VALUES (@id, @tid, @uid, @eid, 'fu-userid.test.event', 'seed', 'b', '/seed', now());";
        cmd.Parameters.Add(new NpgsqlParameter("id", notifId));
        cmd.Parameters.Add(new NpgsqlParameter("tid", tenantId));
        cmd.Parameters.Add(new NpgsqlParameter("uid", userId));
        cmd.Parameters.Add(new NpgsqlParameter("eid", eventId));
        await cmd.ExecuteNonQueryAsync();
        _seededNotificationIds.Add(notifId);
        return notifId;
    }

    private AuditDbContext BuildAuditDbContext(string database, out TenantContext tenant, out UserContext user)
    {
        var ctxTenant = new TenantContext();
        var ctxUser = new UserContext();
        tenant = ctxTenant;
        user = ctxUser;
        var connectionString = BuildAppConnectionString(database);
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString)
            .AddInterceptors(
                new TenantConnectionInterceptor(ctxTenant, ctxUser, NullLogger<TenantConnectionInterceptor>.Instance))
            .Options;
        return new AuditDbContext(options);
    }

    private string BuildAppConnectionString(string database)
        => $"Host=localhost;Port=5432;Database={database};Username=nscim_app;Password={_password};Pooling=false";

    private string BuildAdminConnectionString(string database)
        => $"Host=localhost;Port=5432;Database={database};Username=postgres;Password={_password};Pooling=false";

    public void Dispose()
    {
        if (_password is null) return;
        try
        {
            using var conn = new NpgsqlConnection(BuildAdminConnectionString(PlatformDb));
            conn.Open();
            // Notifications first (FK → events).
            foreach (var id in _seededNotificationIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM audit.notifications WHERE \"Id\" = @id;";
                cmd.Parameters.Add(new NpgsqlParameter("id", id));
                cmd.ExecuteNonQuery();
            }
            foreach (var id in _seededEventIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM audit.events WHERE \"EventId\" = @id;";
                cmd.Parameters.Add(new NpgsqlParameter("id", id));
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // best-effort
        }
    }
}
