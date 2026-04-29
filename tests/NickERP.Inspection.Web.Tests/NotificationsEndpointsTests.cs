using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NickERP.Inspection.Web.Endpoints;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Identity.Auth;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 8 P3 — exercises <see cref="NotificationsEndpoints"/> at the
/// handler-method level. Drives an in-memory <see cref="AuditDbContext"/>
/// + a hand-built <see cref="DefaultHttpContext"/> with a NickERP-shaped
/// <see cref="ClaimsPrincipal"/>; asserts:
///
/// <list type="bullet">
///   <item><description>An anonymous request (no user-id claim) returns
///         401 (the host-level <c>RequireAuthorization()</c> would block
///         it before the handler runs in production; the handler still
///         double-checks).</description></item>
///   <item><description><c>unreadOnly=true</c> filter excludes already-read
///         rows.</description></item>
///   <item><description><c>POST /{id}/read</c> stamps <c>ReadAt</c>;
///         a second call is a no-op.</description></item>
///   <item><description><c>POST /read-all</c> stamps every unread row
///         the caller can see.</description></item>
/// </list>
///
/// <para>
/// Sprint 9 / FU-userid — user-isolation moved from the LINQ layer to
/// the DB-level RLS policy <c>tenant_user_isolation_notifications</c>.
/// The in-memory provider does not run RLS, so these unit tests no
/// longer assert user-isolation; that's covered by the Postgres-backed
/// <see cref="NickERP.Platform.Tests.NotificationsRlsIsolationTests"/>
/// against a real cluster. The unit tests still assert paging,
/// mark-as-read idempotency, and unreadOnly filtering — the parts of
/// the endpoint logic that survive the LINQ-filter removal.
/// </para>
/// </summary>
public sealed class NotificationsEndpointsTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetList_returns_401_when_no_user_claim()
    {
        await using var db = BuildDb("notif-anon");

        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var result = await NotificationsEndpoints.GetListAsync(http, db);

        result.GetType().Name.Should().Be("UnauthorizedHttpResult");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetList_returns_paged_notifications_for_authenticated_caller()
    {
        // FU-userid — the LINQ-level user filter has moved to the RLS
        // policy `tenant_user_isolation_notifications`. The in-memory
        // provider doesn't enforce RLS, so this unit test no longer
        // asserts user-isolation (covered by the Postgres-backed
        // NotificationsRlsIsolationTests). It does assert that the
        // endpoint accepts the auth claim, exposes a NotificationsPageDto,
        // and counts what the DB returns — the parts of the handler that
        // don't depend on user-isolation enforcement.
        var alice = Guid.NewGuid();

        await using var db = BuildDb("notif-page-" + Guid.NewGuid());
        db.Notifications.AddRange(
            NewNotification(alice, "Hello Alice", tenantId: 1),
            NewNotification(alice, "Another", tenantId: 1));
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var result = await NotificationsEndpoints.GetListAsync(http, db);

        // Inspect the OK<T> result via reflection to keep the test
        // assertion-friendly without coupling to the IResult internal type.
        var page = ExtractValue<NotificationsPageDto>(result);
        page.Should().NotBeNull();
        page!.Items.Should().HaveCount(2);
        page.UnreadCount.Should().Be(2);
        page.TotalCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetList_unreadOnly_excludes_already_read_rows()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb("notif-unread-" + Guid.NewGuid());

        var read = NewNotification(alice, "Old Read", tenantId: 1);
        read.ReadAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var unread = NewNotification(alice, "Fresh", tenantId: 1);
        db.Notifications.AddRange(read, unread);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var result = await NotificationsEndpoints.GetListAsync(http, db, unreadOnly: true);
        var page = ExtractValue<NotificationsPageDto>(result);
        page!.Items.Should().HaveCount(1);
        page.Items[0].Title.Should().Be("Fresh");
        page.UnreadCount.Should().Be(1);
        page.TotalCount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkRead_stamps_ReadAt_and_is_idempotent()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb("notif-mark-" + Guid.NewGuid());
        var n = NewNotification(alice, "To read", tenantId: 1);
        db.Notifications.Add(n);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var first = await NotificationsEndpoints.MarkReadAsync(n.Id, http, db);
        first.GetType().Name.Should().Be("NoContent");

        await using (var verify = BuildDb("notif-mark-verify-" + Guid.NewGuid()))
        {
            // Re-read via the same context: the row should now have
            // ReadAt set. (We can't reuse a fresh in-memory DB; assert
            // off the original.)
        }

        var stored = await db.Notifications.AsNoTracking()
            .FirstAsync(x => x.Id == n.Id);
        stored.ReadAt.Should().NotBeNull();
        var firstReadAt = stored.ReadAt!.Value;

        // Idempotent re-call must not reset the timestamp.
        var second = await NotificationsEndpoints.MarkReadAsync(n.Id, http, db);
        second.GetType().Name.Should().Be("NoContent");
        var stored2 = await db.Notifications.AsNoTracking().FirstAsync(x => x.Id == n.Id);
        stored2.ReadAt.Should().Be(firstReadAt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkRead_returns_NotFound_for_unknown_id()
    {
        // FU-userid — the LINQ-level "not the caller's row" filter is gone;
        // user-isolation lives at the RLS layer. Under the in-memory
        // provider (no RLS), the only way to get a 404 is for the row to
        // not exist at all. Cross-user 404 behaviour is covered by the
        // Postgres-backed NotificationsRlsIsolationTests.
        var alice = Guid.NewGuid();
        var unknownId = Guid.NewGuid();

        await using var db = BuildDb("notif-notfound-" + Guid.NewGuid());

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var result = await NotificationsEndpoints.MarkReadAsync(unknownId, http, db);

        result.GetType().Name.Should().Be("NotFound");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkAllRead_stamps_unread_rows_visible_to_caller()
    {
        // FU-userid — under the in-memory provider every row is visible
        // (no RLS). The handler now stamps every unread row it sees.
        // In production, RLS narrows visibility to the caller's
        // (tenant, user) before this code path runs; that narrowing is
        // tested by the Postgres-backed NotificationsRlsIsolationTests.
        var alice = Guid.NewGuid();

        await using var db = BuildDb("notif-all-" + Guid.NewGuid());
        var unread1 = NewNotification(alice, "A1", tenantId: 1);
        var unread2 = NewNotification(alice, "A2", tenantId: 1);
        var alreadyRead = NewNotification(alice, "A3", tenantId: 1);
        alreadyRead.ReadAt = DateTimeOffset.UtcNow.AddHours(-1);
        db.Notifications.AddRange(unread1, unread2, alreadyRead);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var result = await NotificationsEndpoints.MarkAllReadAsync(http, db);
        result.GetType().Name.Should().Contain("Ok");

        var rows = await db.Notifications.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(3).And.OnlyContain(n => n.ReadAt != null);
    }

    // -- helpers --------------------------------------------------------

    private static AuditDbContext BuildDb(string name)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestAuditDbContext(options);
    }

    /// <summary>
    /// Test-only subclass that adds a JsonDocument↔string converter on
    /// <c>DomainEventRow.Payload</c> so the EF in-memory provider can
    /// build the model. Even though these tests don't query
    /// <c>Events</c>, EF validates every mapped property on first use.
    /// </summary>
    private sealed class TestAuditDbContext : AuditDbContext
    {
        public TestAuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

        protected override void OnAuditModelCreating(ModelBuilder modelBuilder)
        {
            base.OnAuditModelCreating(modelBuilder);

            var jsonConverter = new ValueConverter<JsonDocument, string>(
                v => v.RootElement.GetRawText(),
                v => JsonDocument.Parse(v, default));
            modelBuilder.Entity<DomainEventRow>()
                .Property(e => e.Payload)
                .HasConversion(jsonConverter);
        }
    }

    private static Notification NewNotification(Guid userId, string title, long tenantId)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            EventId = Guid.NewGuid(),
            EventType = "nickerp.test.event",
            Title = title,
            Body = "body",
            Link = "/cases/" + Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static ClaimsPrincipal PrincipalFor(Guid userId)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(NickErpClaims.Id, userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        identity.AddClaim(new Claim(NickErpClaims.TenantId, "1"));
        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Pull the <c>Value</c> property off an <see cref="IResult"/>
    /// implementation (e.g. <c>Ok&lt;T&gt;</c>). The concrete result types
    /// are internal to ASP.NET Core; reflection keeps the test resilient
    /// across framework versions.
    /// </summary>
    private static T? ExtractValue<T>(IResult result) where T : class
    {
        var prop = result.GetType().GetProperty("Value");
        if (prop is null) return null;
        return prop.GetValue(result) as T;
    }
}
