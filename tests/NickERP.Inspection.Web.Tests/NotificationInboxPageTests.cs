using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Inspection.Web.Components.Pages.Notifications;
using NickERP.Inspection.Web.Services;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;
using BunitTestContext = Bunit.TestContext;

namespace NickERP.Inspection.Web.Tests;

/// <summary>
/// Sprint 35 / B8.1 — bunit render coverage for the
/// <see cref="Inbox"/> Razor page. Verifies the page renders the
/// filter bar + table when notifications exist, and the empty-state
/// markup when nothing matches the filter.
/// </summary>
public sealed class NotificationInboxPageTests : IDisposable
{
    private readonly BunitTestContext _ctx = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly DateTimeOffset _now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

    public NotificationInboxPageTests()
    {
        var auditName = "inbox-page-" + Guid.NewGuid();
        _ctx.Services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(auditName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        _ctx.Services.AddScoped<AuditDbContext>(sp =>
            new TestAuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));

        _ctx.Services.AddScoped<ITenantContext>(_ =>
        {
            var t = new TenantContext();
            t.SetTenant(1);
            return t;
        });
        _ctx.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        _ctx.Services.AddSingleton<IEventPublisher, NoopEventPublisher>();
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _ctx.Services.AddScoped<NotificationInboxService>();
        _ctx.Services.AddSingleton<AuthenticationStateProvider>(new FakeAuthStateProvider(_userId));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    [Trait("Category", "Integration")]
    public void Inbox_ShowsEmptyState_WhenNoNotifications()
    {
        var cut = _ctx.RenderComponent<Inbox>();
        cut.Markup.Should().Contain("Notifications");
        cut.Markup.Should().Contain("No notifications match the current filters");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Inbox_RendersTableRows_WhenNotificationsExist()
    {
        using (var scope = _ctx.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            db.Notifications.Add(new Notification
            {
                Id = Guid.NewGuid(),
                TenantId = 1,
                UserId = _userId,
                EventId = Guid.NewGuid(),
                EventType = "inspection.case_opened",
                Title = "Case opened",
                Body = "A new case is awaiting your attention.",
                Link = "/cases/abc",
                CreatedAt = _now,
            });
            db.SaveChanges();
        }

        var cut = _ctx.RenderComponent<Inbox>();

        cut.Markup.Should().Contain("Notifications");
        cut.Markup.Should().Contain("Case opened");
        cut.Markup.Should().Contain("inspection.case_opened");
        cut.Markup.Should().Contain("Mark as read");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Inbox_RendersUnreadOnlyOption()
    {
        // Filter bar always renders; even when no rows the page lets
        // operators flip the read-state filter and clear filters.
        var cut = _ctx.RenderComponent<Inbox>();
        cut.Markup.Should().Contain("Unread only");
        cut.Markup.Should().Contain("Clear filters");
    }

    private sealed class FakeAuthStateProvider : AuthenticationStateProvider
    {
        private readonly Guid _userId;
        public FakeAuthStateProvider(Guid userId) => _userId = userId;
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "Test User"),
                new Claim("nickerp:id", _userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim("nickerp:tenant_id", "1"),
            }, "Test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class NoopEventPublisher : IEventPublisher
    {
        public Task<DomainEvent> PublishAsync(DomainEvent evt, CancellationToken ct = default) => Task.FromResult(evt);
        public Task<IReadOnlyList<DomainEvent>> PublishBatchAsync(IReadOnlyList<DomainEvent> events, CancellationToken ct = default)
            => Task.FromResult(events);
    }

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
}
