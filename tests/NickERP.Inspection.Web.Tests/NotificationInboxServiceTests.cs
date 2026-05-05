using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 35 / B8.1 — coverage for <see cref="NotificationInboxService"/>.
/// Drives the in-memory provider so the LINQ shape (filter / paging
/// / count / mark-read idempotency) is verified without booting Postgres.
/// Audit emission is captured via a stand-in <see cref="IEventPublisher"/>.
/// RLS-level user-isolation is covered by the Postgres-backed
/// <c>NotificationsRlsIsolationTests</c> in the platform test project.
/// </summary>
public sealed class NotificationInboxServiceTests
{
    private readonly DateTimeOffset _now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly List<DomainEvent> _events = new();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_FiltersByUser()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await using var db = BuildDb();
        db.Notifications.AddRange(
            NewNotification(alice, "alice 1", tenantId: 1),
            NewNotification(alice, "alice 2", tenantId: 1),
            NewNotification(bob, "bob 1", tenantId: 1));
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var page = await svc.ListAsync(alice, NotificationInboxFilter.All);

        page.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(r => r.Title.StartsWith("alice"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_UnreadOnlyFilter_ExcludesReadRows()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var read = NewNotification(alice, "old", tenantId: 1);
        read.ReadAt = _now.AddMinutes(-5);
        var unread = NewNotification(alice, "fresh", tenantId: 1);
        db.Notifications.AddRange(read, unread);
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var page = await svc.ListAsync(
            alice,
            new NotificationInboxFilter(ReadState: NotificationReadState.UnreadOnly));

        page.Items.Should().ContainSingle().Which.Title.Should().Be("fresh");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_ReadOnlyFilter_ExcludesUnreadRows()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var read = NewNotification(alice, "old", tenantId: 1);
        read.ReadAt = _now.AddMinutes(-5);
        var unread = NewNotification(alice, "fresh", tenantId: 1);
        db.Notifications.AddRange(read, unread);
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var page = await svc.ListAsync(
            alice,
            new NotificationInboxFilter(ReadState: NotificationReadState.ReadOnly));

        page.Items.Should().ContainSingle().Which.Title.Should().Be("old");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_EventTypeFilter()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        db.Notifications.AddRange(
            NewNotification(alice, "case-opened", tenantId: 1, eventType: "inspection.case_opened"),
            NewNotification(alice, "case-assigned", tenantId: 1, eventType: "inspection.case_assigned"));
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var page = await svc.ListAsync(
            alice,
            new NotificationInboxFilter(EventType: "inspection.case_assigned"));

        page.Items.Should().ContainSingle().Which.Title.Should().Be("case-assigned");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_DateRangeFilter()
    {
        var alice = Guid.NewGuid();
        var early = _now.AddDays(-10);
        var middle = _now.AddDays(-3);
        var late = _now;

        await using var db = BuildDb();
        var n1 = NewNotification(alice, "early", tenantId: 1);
        n1.CreatedAt = early;
        var n2 = NewNotification(alice, "middle", tenantId: 1);
        n2.CreatedAt = middle;
        var n3 = NewNotification(alice, "late", tenantId: 1);
        n3.CreatedAt = late;
        db.Notifications.AddRange(n1, n2, n3);
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var page = await svc.ListAsync(
            alice,
            new NotificationInboxFilter(From: _now.AddDays(-5), To: _now.AddDays(-1)));

        page.Items.Should().ContainSingle().Which.Title.Should().Be("middle");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_PaginatesNewestFirst()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        for (int i = 0; i < 25; i++)
        {
            var n = NewNotification(alice, $"row-{i:D2}", tenantId: 1);
            n.CreatedAt = _now.AddMinutes(-i);
            db.Notifications.Add(n);
        }
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var page1 = await svc.ListAsync(alice, NotificationInboxFilter.All, take: 10, skip: 0);
        var page2 = await svc.ListAsync(alice, NotificationInboxFilter.All, take: 10, skip: 10);
        var page3 = await svc.ListAsync(alice, NotificationInboxFilter.All, take: 10, skip: 20);

        page1.TotalCount.Should().Be(25);
        page1.Items.Should().HaveCount(10);
        page2.Items.Should().HaveCount(10);
        page3.Items.Should().HaveCount(5);

        // Newest-first: page1 row 0 has the newest CreatedAt.
        page1.Items.First().Title.Should().Be("row-00");
        page2.Items.First().Title.Should().Be("row-10");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_UnreadCount_AlwaysReflectsUnreadOnly()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var read = NewNotification(alice, "read", tenantId: 1);
        read.ReadAt = _now.AddMinutes(-5);
        db.Notifications.AddRange(
            read,
            NewNotification(alice, "unread-1", tenantId: 1),
            NewNotification(alice, "unread-2", tenantId: 1));
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        // UnreadCount stays at 2 regardless of the filter.
        var allPage = await svc.ListAsync(alice, NotificationInboxFilter.All);
        var unreadPage = await svc.ListAsync(alice,
            new NotificationInboxFilter(ReadState: NotificationReadState.UnreadOnly));

        allPage.UnreadCount.Should().Be(2);
        unreadPage.UnreadCount.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnreadCountAsync_ReturnsUnreadOnly()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var read = NewNotification(alice, "read", tenantId: 1);
        read.ReadAt = _now.AddMinutes(-5);
        db.Notifications.AddRange(
            read,
            NewNotification(alice, "u1", tenantId: 1),
            NewNotification(alice, "u2", tenantId: 1));
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var n = await svc.UnreadCountAsync(alice);
        n.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnreadCountAsync_ReturnsZeroForEmptyUserId()
    {
        await using var db = BuildDb();
        var svc = BuildService(db);

        (await svc.UnreadCountAsync(Guid.Empty)).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkReadAsync_FlipsReadAtAndEmitsEvent()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var n = NewNotification(alice, "to-read", tenantId: 1);
        db.Notifications.Add(n);
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var flipped = await svc.MarkReadAsync(n.Id, alice);

        flipped.Should().BeTrue();
        var stored = await db.Notifications.AsNoTracking().FirstAsync(x => x.Id == n.Id);
        stored.ReadAt.Should().Be(_now);

        _events.Should().ContainSingle()
            .Which.EventType.Should().Be("nickerp.notification.read");
        var evt = _events.Single();
        evt.EntityType.Should().Be("Notification");
        evt.EntityId.Should().Be(n.Id.ToString());
        evt.ActorUserId.Should().Be(alice);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkReadAsync_IsIdempotent()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var n = NewNotification(alice, "to-read", tenantId: 1);
        db.Notifications.Add(n);
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var first = await svc.MarkReadAsync(n.Id, alice);
        var second = await svc.MarkReadAsync(n.Id, alice);

        first.Should().BeTrue();
        second.Should().BeFalse(because: "row was already read on the second call");

        _events.Should().ContainSingle(because: "no event for the no-op second call");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkReadAsync_ReturnsFalse_ForUnknownId()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var svc = BuildService(db);

        var flipped = await svc.MarkReadAsync(Guid.NewGuid(), alice);

        flipped.Should().BeFalse();
        _events.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkAllReadAsync_FlipsEveryUnreadRow()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var read = NewNotification(alice, "old", tenantId: 1);
        read.ReadAt = _now.AddHours(-1);
        db.Notifications.AddRange(
            read,
            NewNotification(alice, "u1", tenantId: 1),
            NewNotification(alice, "u2", tenantId: 1));
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var n = await svc.MarkAllReadAsync(alice);

        n.Should().Be(2);
        var rows = await db.Notifications.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.ReadAt != null);

        _events.Should().HaveCount(2);
        _events.Should().AllSatisfy(e => e.EventType.Should().Be("nickerp.notification.read"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MarkAllReadAsync_ReturnsZeroWhenNothingUnread()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        var svc = BuildService(db);

        var n = await svc.MarkAllReadAsync(alice);

        n.Should().Be(0);
        _events.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListEventTypesAsync_ReturnsDistinctSorted()
    {
        var alice = Guid.NewGuid();
        await using var db = BuildDb();
        db.Notifications.AddRange(
            NewNotification(alice, "x", tenantId: 1, eventType: "inspection.case_opened"),
            NewNotification(alice, "y", tenantId: 1, eventType: "inspection.case_opened"),
            NewNotification(alice, "z", tenantId: 1, eventType: "inspection.case_assigned"));
        await db.SaveChangesAsync();
        var svc = BuildService(db);

        var types = await svc.ListEventTypesAsync(alice);

        types.Should().Equal("inspection.case_assigned", "inspection.case_opened");
    }

    // -- helpers --------------------------------------------------------

    private NotificationInboxService BuildService(AuditDbContext db)
    {
        var clock = new FakeTimeProvider(_now);
        var publisher = new CapturingEventPublisher(_events);
        var tenant = new TenantContext();
        tenant.SetTenant(1);
        return new NotificationInboxService(
            db, clock, tenant, publisher,
            NullLogger<NotificationInboxService>.Instance);
    }

    private static AuditDbContext BuildDb()
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase("inbox-svc-" + Guid.NewGuid())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TestAuditDbContext(options);
    }

    /// <summary>
    /// Test-only subclass that adds a JsonDocument↔string converter so
    /// EF in-memory can build the model. Mirrors the pattern in
    /// <c>AuditNotificationProjectorTests.TestAuditDbContext</c>.
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

    private static Notification NewNotification(
        Guid userId, string title, long tenantId,
        string eventType = "nickerp.test.event")
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Title = title,
            Body = "body",
            Link = "/cases/" + Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class CapturingEventPublisher : IEventPublisher
    {
        private readonly List<DomainEvent> _sink;
        public CapturingEventPublisher(List<DomainEvent> sink) => _sink = sink;
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default)
        {
            _sink.Add(evt);
            return Task.FromResult(evt);
        }
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
        {
            _sink.AddRange(events);
            return Task.FromResult(events);
        }
    }
}
