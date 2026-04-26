using Microsoft.EntityFrameworkCore;

namespace NickERP.Platform.Audit.Database;

/// <summary>
/// EF Core DbContext for the canonical event log. Schema <c>audit</c>
/// inside the <c>nickerp_platform</c> Postgres DB (sibling to
/// <c>identity</c> and <c>tenancy</c>).
/// </summary>
/// <remarks>
/// The DbContext exposes only an internal <c>DbSet</c> — application code
/// never queries the events table directly through this DbContext. Reads
/// happen via the future audit admin REST API (and the search UI in the
/// Portal); writes happen through <see cref="DbEventPublisher"/>.
///
/// Append-only enforcement at the role level (<c>REVOKE UPDATE, DELETE</c>)
/// is an ops task documented in <c>AUDIT.md</c>; the DbContext does not
/// expose update/delete codepaths, but a bad actor with DB access could
/// still mutate rows unless the role grants are tightened.
/// </remarks>
public sealed class AuditDbContext : DbContext
{
    public const string SchemaName = "audit";

    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

    internal DbSet<DomainEventRow> Events => Set<DomainEventRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<DomainEventRow>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.ActorUserId);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.EventType).IsRequired().HasMaxLength(200);
            e.Property(x => x.EntityType).IsRequired().HasMaxLength(100);
            e.Property(x => x.EntityId).IsRequired().HasMaxLength(200);
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.OccurredAt).IsRequired();
            e.Property(x => x.IngestedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(128);
            e.Property(x => x.PrevEventHash).HasMaxLength(64);

            // Idempotency dedup — same key inside the same tenant is a single event.
            e.HasIndex(x => new { x.TenantId, x.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("ux_audit_events_tenant_idempotency");

            // Hot-path lookup: events for a given entity, time-ordered.
            e.HasIndex(x => new { x.TenantId, x.EntityType, x.EntityId, x.OccurredAt })
                .HasDatabaseName("ix_audit_events_entity_time");

            // Hot-path lookup: events of a given type for a tenant, time-ordered.
            e.HasIndex(x => new { x.TenantId, x.EventType, x.OccurredAt })
                .HasDatabaseName("ix_audit_events_type_time");

            // Audit-by-actor.
            e.HasIndex(x => new { x.TenantId, x.ActorUserId, x.OccurredAt })
                .HasDatabaseName("ix_audit_events_actor_time");

            // Cross-service correlation: pin one trace, see all events.
            e.HasIndex(x => x.CorrelationId)
                .HasDatabaseName("ix_audit_events_correlation");
        });
    }
}

/// <summary>Design-time factory; same env-var convention as the other platform DbContexts.</summary>
public sealed class AuditDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_platform;Username=postgres;Password=designtime";

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AuditDbContext).Assembly.GetName().Name))
            .Options;

        return new AuditDbContext(options);
    }
}
