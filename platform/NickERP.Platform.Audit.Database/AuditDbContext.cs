using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Audit.Database.Entities;

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
public class AuditDbContext : DbContext
{
    public const string SchemaName = "audit";

    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options) { }

    /// <summary>
    /// Audit-event rows. Exposed publicly so the audit-admin UI (Portal) can
    /// read them. Modules NEVER query this directly — they emit via
    /// <see cref="Events.IEventPublisher"/>. This DbSet is read-mostly; the
    /// only writer is <see cref="DbEventPublisher"/>.
    /// </summary>
    public DbSet<DomainEventRow> Events => Set<DomainEventRow>();

    /// <summary>
    /// User-facing notifications, written by
    /// <c>AuditNotificationProjector</c> as it projects
    /// <see cref="Events"/> rows through registered notification rules
    /// (Sprint 8 P3). Tenant-isolated via RLS; user-isolation enforced at
    /// the LINQ layer (no <c>app.user_id</c> session setting today).
    /// </summary>
    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>
    /// Per-projector bookmark table. Single row per projector name; the
    /// row carries the latest <c>IngestedAt</c> the projector has already
    /// processed so the next tick can <c>WHERE IngestedAt &gt; checkpoint</c>.
    /// Intentionally NOT under RLS — system bookmark, no tenant payload.
    /// </summary>
    public DbSet<ProjectionCheckpoint> ProjectionCheckpoints => Set<ProjectionCheckpoint>();

    /// <summary>
    /// Sprint 11 / P2 — pre-configured (edge_node_id, tenant_id) pairs
    /// the server uses to authorize incoming edge replay batches. Suite-
    /// wide reference data; intentionally NOT under tenant RLS.
    /// </summary>
    public DbSet<EdgeNodeAuthorization> EdgeNodeAuthorizations => Set<EdgeNodeAuthorization>();

    /// <summary>
    /// Sprint 11 / P2 — one row per replay batch processed by the
    /// server. Operator visibility into edge activity; not under tenant
    /// RLS (a single batch can carry multiple tenants).
    /// </summary>
    public DbSet<EdgeNodeReplayLog> EdgeNodeReplayLogs => Set<EdgeNodeReplayLog>();

    /// <summary>
    /// Sprint 13 / P2-FU-edge-auth — per-edge-node API keys used by the
    /// edge auth handler to authenticate incoming
    /// <c>/api/edge/replay</c> requests. Tenant-scoped + RLS-enforced;
    /// the auth handler reads under <c>SetSystemContext</c> so the
    /// initial lookup can hit any key, then asserts the row's tenant
    /// matches the request's authorized-tenant set after the fact.
    /// </summary>
    public DbSet<EdgeNodeApiKey> EdgeNodeApiKeys => Set<EdgeNodeApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // virtual so test subclasses can layer in additional model
        // configuration (e.g. a JsonDocument↔string converter for the EF
        // in-memory provider, which can't natively map jsonb).
        OnAuditModelCreating(modelBuilder);
    }

    /// <summary>
    /// Production model definition. Pulled out of <see cref="OnModelCreating"/>
    /// so tests can subclass and add provider-specific configuration on top.
    /// </summary>
    protected virtual void OnAuditModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<DomainEventRow>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.EventId);
            e.Property(x => x.EventId).HasDefaultValueSql("gen_random_uuid()");
            // G1 #4 — TenantId is nullable. Most events carry a concrete
            // tenant; suite-wide system events (FX rates, global config)
            // are NULL-tenant.
            e.Property(x => x.TenantId);
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

            // G1 #4 — partial index for the system-event case
            // (TenantId IS NULL). Suite-wide events (FX rates, global
            // chart-of-accounts) want fast time-ordered enumeration
            // without scanning the per-tenant rows.
            e.HasIndex(x => new { x.EventType, x.OccurredAt })
                .HasDatabaseName("ix_audit_events_system_type_time")
                .HasFilter("\"TenantId\" IS NULL");
        });

        // ---- Sprint 8 P3 — audit.notifications -----------------------
        modelBuilder.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.EventId).IsRequired();
            e.Property(x => x.EventType).IsRequired().HasMaxLength(200);
            e.Property(x => x.Title).IsRequired().HasMaxLength(200);
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.Link).HasMaxLength(500);
            e.Property(x => x.CreatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.ReadAt);

            // FK to audit.events.EventId. Cascade delete is intentional: if
            // an audit row is removed (administrative purge — never in
            // normal operation), drop the notifications too.
            e.HasOne<DomainEventRow>()
                .WithMany()
                .HasForeignKey(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            // Idempotency: re-projecting the same event for the same user
            // is a no-op. The projector relies on this unique index to
            // make replays safe (combined with the checkpoint, replays
            // shouldn't happen in steady state — but a crash mid-tick can
            // re-process events whose checkpoint hadn't been advanced yet).
            e.HasIndex(x => new { x.UserId, x.EventId })
                .IsUnique()
                .HasDatabaseName("ux_notifications_user_event");

            // Hot-path index for the inbox query: unread rows for a user
            // in a tenant, newest-first. Partial index on ReadAt IS NULL
            // keeps it small as old read rows don't pollute it.
            e.HasIndex(x => new { x.UserId, x.TenantId })
                .HasDatabaseName("ix_notifications_user_unread")
                .HasFilter("\"ReadAt\" IS NULL");
        });

        // ---- Sprint 8 P3 — audit.projection_checkpoints --------------
        modelBuilder.Entity<ProjectionCheckpoint>(e =>
        {
            e.ToTable("projection_checkpoints");
            e.HasKey(x => x.ProjectionName);
            e.Property(x => x.ProjectionName).HasMaxLength(100);
            e.Property(x => x.LastIngestedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        // ---- Sprint 11 / P2 — audit.edge_node_authorizations ---------
        modelBuilder.Entity<EdgeNodeAuthorization>(e =>
        {
            e.ToTable("edge_node_authorizations");
            e.HasKey(x => new { x.EdgeNodeId, x.TenantId });
            e.Property(x => x.EdgeNodeId).IsRequired().HasMaxLength(100);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.AuthorizedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.AuthorizedByUserId);
        });

        // ---- Sprint 11 / P2 — audit.edge_node_replay_log -------------
        modelBuilder.Entity<EdgeNodeReplayLog>(e =>
        {
            e.ToTable("edge_node_replay_log");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.EdgeNodeId).IsRequired().HasMaxLength(100);
            e.Property(x => x.ReplayedAt).IsRequired();
            e.Property(x => x.EventCount).IsRequired();
            e.Property(x => x.OkCount).IsRequired();
            e.Property(x => x.FailedCount).IsRequired();
            e.Property(x => x.FailuresJson).HasColumnType("jsonb");

            // Hot-path lookup: an admin "edge activity for X" page
            // wants the latest batches per edge.
            e.HasIndex(x => new { x.EdgeNodeId, x.ReplayedAt })
                .HasDatabaseName("ix_edge_node_replay_log_edge_time");
        });

        // ---- Sprint 13 / P2-FU-edge-auth — audit.edge_node_api_keys --
        modelBuilder.Entity<EdgeNodeApiKey>(e =>
        {
            e.ToTable("edge_node_api_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.EdgeNodeId).IsRequired().HasMaxLength(100);
            e.Property(x => x.KeyHash).IsRequired();
            e.Property(x => x.KeyPrefix).IsRequired().HasMaxLength(8);
            e.Property(x => x.IssuedAt).IsRequired().HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.Property(x => x.ExpiresAt);
            e.Property(x => x.RevokedAt);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.CreatedByUserId);

            // Auth-handler hot path: lookup-by-hash. UNIQUE so two
            // independent issuances of the same plaintext (impossible
            // with a CSPRNG, but defence-in-depth) collide loudly
            // rather than silently authenticating against either row.
            e.HasIndex(x => x.KeyHash)
                .IsUnique()
                .HasDatabaseName("ux_edge_node_api_keys_keyhash");

            // Operator UI: list keys for a given edge node.
            e.HasIndex(x => new { x.TenantId, x.EdgeNodeId })
                .HasDatabaseName("ix_edge_node_api_keys_tenant_edge");
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
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AuditDbContext).Assembly.GetName().Name);
                // H3 — keep EF Core's history table inside the audit
                // schema so nscim_app never needs CREATE on `public`.
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "audit");
            })
            .Options;

        return new AuditDbContext(options);
    }
}
