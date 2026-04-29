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
///   <item><description>A user from tenant A never sees notifications
///         belonging to tenant B / user B (LINQ-based user-isolation;
///         RLS would also filter at the DB layer in prod, but the
///         in-memory provider doesn't run RLS so this is the inner
///         ring's regression test).</description></item>
///   <item><description><c>unreadOnly=true</c> filter excludes already-read
///         rows.</description></item>
///   <item><description><c>POST /{id}/read</c> stamps <c>ReadAt</c>;
///         a second call is a no-op.</description></item>
///   <item><description><c>POST /read-all</c> stamps every unread row.</description></item>
/// </list>
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
    public async Task GetList_filters_to_caller_user_only()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await using var db = BuildDb("notif-iso-" + Guid.NewGuid());
        db.Notifications.AddRange(
            NewNotification(alice, "Hello Alice", tenantId: 1),
            NewNotification(bob, "Hello Bob", tenantId: 1),
            NewNotification(bob, "Bob Two", tenantId: 1));
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var result = await NotificationsEndpoints.GetListAsync(http, db);

        // Inspect the OK<T> result via reflection to keep the test
        // assertion-friendly without coupling to the IResult internal type.
        var page = ExtractValue<NotificationsPageDto>(result);
        page.Should().NotBeNull();
        page!.Items.Should().HaveCount(1);
        page.Items[0].Title.Should().Be("Hello Alice");
        page.UnreadCount.Should().Be(1);
        page.TotalCount.Should().Be(1);
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
    public async Task MarkRead_returns_NotFound_for_other_users_row()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await using var db = BuildDb("notif-iso-mark-" + Guid.NewGuid());
        var bobsRow = NewNotification(bob, "Private to Bob", tenantId: 1);
        db.Notifications.Add(bobsRow);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var result = await NotificationsEndpoints.MarkReadAsync(bobsRow.Id, http, db);

        result.GetType().Name.Should().Be("NotFound");

        // Bob's row remains unread — Alice didn't touch it.
        var stored = await db.Notifications.AsNoTracking().FirstAsync(x => x.Id == bobsRow.Id);
        stored.ReadAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkAllRead_stamps_only_callers_unread_rows()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await using var db = BuildDb("notif-all-" + Guid.NewGuid());
        var aliceUnread1 = NewNotification(alice, "A1", tenantId: 1);
        var aliceUnread2 = NewNotification(alice, "A2", tenantId: 1);
        var aliceRead = NewNotification(alice, "A3", tenantId: 1);
        aliceRead.ReadAt = DateTimeOffset.UtcNow.AddHours(-1);
        var bobUnread = NewNotification(bob, "B1", tenantId: 1);
        db.Notifications.AddRange(aliceUnread1, aliceUnread2, aliceRead, bobUnread);
        await db.SaveChangesAsync();

        var http = new DefaultHttpContext { User = PrincipalFor(alice) };
        var result = await NotificationsEndpoints.MarkAllReadAsync(http, db);
        result.GetType().Name.Should().Contain("Ok");

        var aliceRows = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == alice).ToListAsync();
        aliceRows.Should().HaveCount(3).And.OnlyContain(n => n.ReadAt != null);

        var bobRow = await db.Notifications.AsNoTracking()
            .FirstAsync(n => n.UserId == bob);
        bobRow.ReadAt.Should().BeNull("Alice's mark-all must not touch Bob's notifications");
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
