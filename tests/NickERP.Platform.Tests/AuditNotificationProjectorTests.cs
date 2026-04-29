using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.Platform.Audit.Database;
using NickERP.Platform.Audit.Database.Entities;
using NickERP.Platform.Audit.Database.Services;
using NickERP.Platform.Audit.Database.Services.NotificationRules;
using NickERP.Platform.Tenancy;

namespace NickERP.Platform.Tests;

/// <summary>
/// Sprint 8 P3 — verifies the <see cref="AuditNotificationProjector"/>
/// projects <c>audit.events</c> rows into <c>audit.notifications</c> via
/// the registered rules and respects checkpoint idempotency.
///
/// <para>
/// EF in-memory provider lets us drive the projector deterministically
/// without spinning Postgres. RLS is skipped by the in-memory provider —
/// fine, because the projector's correctness w.r.t. fan-out, dedup, and
/// checkpoint-advance is what we're asserting; RLS enforcement is
/// covered by <see cref="SystemContextTests"/> against real Postgres.
/// </para>
/// </summary>
public sealed class AuditNotificationProjectorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProjectOnce_writes_one_notification_per_matched_rule()
    {
        var dbName = "audit-notifications-" + Guid.NewGuid();
        var sp = BuildServices(dbName);

        var openerUserId = Guid.NewGuid();
        var caseId = Guid.NewGuid();

        // Seed: one case_opened event for tenant 1.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            db.Events.Add(new DomainEventRow
            {
                EventId = Guid.NewGuid(),
                TenantId = 1,
                ActorUserId = openerUserId,
                EventType = "nickerp.inspection.case_opened",
                EntityType = "InspectionCase",
                EntityId = caseId.ToString(),
                Payload = JsonDocument.Parse("{}"),
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IngestedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IdempotencyKey = "ipk-" + Guid.NewGuid()
            });
            await db.SaveChangesAsync();
        }

        var projector = sp.GetRequiredService<AuditNotificationProjector>();
        var inserted = await projector.ProjectOnceAsync(CancellationToken.None);
        inserted.Should().Be(1);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var rows = await db.Notifications.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(1);
            var n = rows.Single();
            n.UserId.Should().Be(openerUserId);
            n.TenantId.Should().Be(1L);
            n.EventType.Should().Be("nickerp.inspection.case_opened");
            n.Title.Should().Be("Case opened");
            n.Body.Should().Contain(caseId.ToString());
            n.Link.Should().Be($"/cases/{caseId}");
            n.ReadAt.Should().BeNull();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProjectOnce_is_idempotent_against_re_run_via_checkpoint()
    {
        var dbName = "audit-notifications-idem-" + Guid.NewGuid();
        var sp = BuildServices(dbName);

        var openerUserId = Guid.NewGuid();
        var caseId = Guid.NewGuid();

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            db.Events.Add(new DomainEventRow
            {
                EventId = Guid.NewGuid(),
                TenantId = 1,
                ActorUserId = openerUserId,
                EventType = "nickerp.inspection.case_opened",
                EntityType = "InspectionCase",
                EntityId = caseId.ToString(),
                Payload = JsonDocument.Parse("{}"),
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IngestedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IdempotencyKey = "ipk-" + Guid.NewGuid()
            });
            await db.SaveChangesAsync();
        }

        var projector = sp.GetRequiredService<AuditNotificationProjector>();

        var first = await projector.ProjectOnceAsync(CancellationToken.None);
        first.Should().Be(1, "the first tick projects the seeded event");

        var second = await projector.ProjectOnceAsync(CancellationToken.None);
        second.Should().Be(0,
            "the checkpoint advanced past the seeded event so the second tick has no work");

        using var verifyScope = sp.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await verifyDb.Notifications.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(1, "no duplicate row was created on the idempotent re-run");

        var checkpoint = await verifyDb.ProjectionCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ProjectionName == AuditNotificationProjector.ProjectorName);
        checkpoint.Should().NotBeNull();
        checkpoint!.LastIngestedAt.Should().BeAfter(DateTimeOffset.MinValue,
            "the checkpoint advanced after the first tick");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProjectOnce_runs_under_SystemContext_and_lands_a_row()
    {
        // Sprint 9 / FU-userid — sanity check that the per-tenant insert
        // path now flips the ITenantContext into SetSystemContext mode.
        // The in-memory provider doesn't actually run RLS so this test
        // can't observe the OR clause directly — but it proves the
        // projector reaches SaveChangesAsync without raising, the row
        // lands in audit.notifications, and the shared TenantContext
        // ends in IsSystem == true (the state the projector leaves it
        // in). The Postgres-backed NotificationsRlsIsolationTests cover
        // the OR-clause behaviour against real RLS.
        var dbName = "audit-notifications-system-" + Guid.NewGuid();
        var sp = BuildServices(dbName);

        var openerUserId = Guid.NewGuid();
        var caseId = Guid.NewGuid();

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            db.Events.Add(new DomainEventRow
            {
                EventId = Guid.NewGuid(),
                TenantId = 7,
                ActorUserId = openerUserId,
                EventType = "nickerp.inspection.case_opened",
                EntityType = "InspectionCase",
                EntityId = caseId.ToString(),
                Payload = JsonDocument.Parse("{}"),
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IngestedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IdempotencyKey = "ipk-" + Guid.NewGuid()
            });
            await db.SaveChangesAsync();
        }

        var projector = sp.GetRequiredService<AuditNotificationProjector>();
        var inserted = await projector.ProjectOnceAsync(CancellationToken.None);
        inserted.Should().Be(1);

        using var verifyScope = sp.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await verifyDb.Notifications.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].TenantId.Should().Be(7L,
            "the per-tenant fan-out narrows audit.events by tenantId in LINQ "
            + "even though RLS is permissive under system context");
        rows[0].UserId.Should().Be(openerUserId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProjectOnce_reads_AnalystUserId_from_payload_for_case_assigned()
    {
        var dbName = "audit-notifications-assigned-" + Guid.NewGuid();
        var sp = BuildServices(dbName);

        var analyst = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var caseId = Guid.NewGuid();

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            db.Events.Add(new DomainEventRow
            {
                EventId = Guid.NewGuid(),
                TenantId = 1,
                ActorUserId = actor,
                EventType = "nickerp.inspection.case_assigned",
                EntityType = "InspectionCase",
                EntityId = caseId.ToString(),
                Payload = JsonDocument.Parse(
                    JsonSerializer.Serialize(new { Id = caseId, AnalystUserId = analyst })),
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IngestedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                IdempotencyKey = "ipk-" + Guid.NewGuid()
            });
            await db.SaveChangesAsync();
        }

        var projector = sp.GetRequiredService<AuditNotificationProjector>();
        var inserted = await projector.ProjectOnceAsync(CancellationToken.None);
        inserted.Should().Be(1);

        using var verifyScope = sp.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var n = await verifyDb.Notifications.AsNoTracking().SingleAsync();
        n.UserId.Should().Be(analyst, "case_assigned notifies the analyst, not the actor");
        n.Title.Should().Contain("assigned");
    }

    /// <summary>
    /// Build a service provider hosting an in-memory AuditDbContext, a
    /// real <see cref="TenantContext"/>, the three notification rules,
    /// and the projector itself. Mirrors the production wiring exactly
    /// except it skips the DbContextOptions interceptors (the in-memory
    /// provider doesn't execute them and they'd error on null
    /// connections), and substitutes a JsonDocument-aware test context
    /// (the in-memory provider can't natively map the jsonb column type).
    /// </summary>
    private static IServiceProvider BuildServices(string dbName)
    {
        var services = new ServiceCollection();
        // Register AuditDbContext with the in-memory provider, then
        // override the factory to instantiate TestAuditDbContext (which
        // adds the JsonDocument↔string converter the in-memory provider
        // needs). The base options are resolved via the standard registration.
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        // Replace the AuditDbContext registration so it returns
        // TestAuditDbContext using the same options.
        services.AddScoped<AuditDbContext>(sp =>
            new TestAuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<INotificationRule, CaseOpenedRule>();
        services.AddScoped<INotificationRule, CaseAssignedRule>();
        services.AddScoped<INotificationRule, CaseVerdictRenderedRule>();
        services.AddSingleton<IOptions<AuditNotificationProjectorOptions>>(
            Options.Create(new AuditNotificationProjectorOptions
            {
                PollIntervalSeconds = 1,
                BatchSize = 50
            }));
        services.AddSingleton<AuditNotificationProjector>(sp => new AuditNotificationProjector(
            sp,
            sp.GetRequiredService<IOptions<AuditNotificationProjectorOptions>>(),
            NullLogger<AuditNotificationProjector>.Instance));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Test-only subclass that adds a JsonDocument↔string value converter
    /// on <c>DomainEventRow.Payload</c> so the EF in-memory provider can
    /// materialise the column. Production runs use Postgres jsonb directly
    /// and never hit this converter.
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
}
