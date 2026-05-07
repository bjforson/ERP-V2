using Microsoft.EntityFrameworkCore;
using NickERP.Platform.Queueing.Entities;

namespace NickERP.Platform.Queueing.Database;

/// <summary>
/// EF Core DbContext for the platform-wide queueing tables (outbox +
/// dead-letter). Schema <c>queueing</c> inside the
/// <c>nickerp_platform</c> Postgres DB (sibling to <c>tenancy</c>,
/// <c>identity</c>, <c>audit</c>). Per-module queue tables live in the
/// owning module's <c>*.Database</c> DbContext; this context owns only
/// the cross-module concerns.
/// </summary>
public sealed class QueueingDbContext : DbContext
{
    public const string SchemaName = "queueing";

    public QueueingDbContext(DbContextOptions<QueueingDbContext> options) : base(options) { }

    /// <summary>Cross-system messages awaiting delivery via the <c>OutboxRelay</c>.</summary>
    public DbSet<QueueOutboxRow> Outbox => Set<QueueOutboxRow>();

    /// <summary>Rows promoted from any queue after exceeding their retry budget.</summary>
    public DbSet<QueueDeadLetterRow> DeadLetter => Set<QueueDeadLetterRow>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        ConfigureQueueRowBase<QueueOutboxRow>(modelBuilder, "outbox");
        modelBuilder.Entity<QueueOutboxRow>(e =>
        {
            e.Property(x => x.DestinationKind).IsRequired().HasMaxLength(64);
            e.Property(x => x.DestinationName).IsRequired().HasMaxLength(256);
            e.Property(x => x.ContentType).IsRequired().HasMaxLength(128).HasDefaultValue("application/json");
            // Index on (destination_kind, available_at) for the relay's
            // adapter-aware claim path; today the relay claims kind-agnostic
            // but ops may want per-kind dashboards.
            e.HasIndex(x => new { x.DestinationKind, x.AvailableAt })
                .HasDatabaseName("ix_outbox_kind_availableat");
        });

        ConfigureQueueRowBase<QueueDeadLetterRow>(modelBuilder, "dead_letter");
        modelBuilder.Entity<QueueDeadLetterRow>(e =>
        {
            e.Property(x => x.SourceQueue).IsRequired().HasMaxLength(128);
            e.Property(x => x.SourceQueueRowId).IsRequired();
            e.Property(x => x.PromotedAt).IsRequired().HasDefaultValueSql("now()");
            // Index on source_queue for the DLQ admin UI's "filter by
            // queue" view.
            e.HasIndex(x => new { x.SourceQueue, x.PromotedAt })
                .IsDescending(false, true)
                .HasDatabaseName("ix_dead_letter_sourcequeue_promotedat");
        });
    }

    /// <summary>
    /// Apply the shared <see cref="QueueRow"/> column shape to a derived
    /// concrete row type. Centralised so the outbox + dead-letter +
    /// any future shared queue tables stay schema-identical for the
    /// columns the platform owns.
    /// </summary>
    private static void ConfigureQueueRowBase<T>(ModelBuilder modelBuilder, string tableName) where T : QueueRow
    {
        modelBuilder.Entity<T>(e =>
        {
            e.ToTable(tableName);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.WorkItemId).IsRequired();
            e.Property(x => x.EnqueuedAt).IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.AvailableAt).IsRequired().HasDefaultValueSql("now()");
            e.Property(x => x.ClaimedBy).HasMaxLength(128);
            e.Property(x => x.ClaimedUntil);
            e.Property(x => x.AttemptCount).IsRequired().HasDefaultValue(0);
            e.Property(x => x.Payload).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
            e.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(128);
            e.Property(x => x.LastError).HasColumnType("text");
            e.Property(x => x.CorrelationId).HasMaxLength(128);
            e.Property(x => x.CreatedAt).IsRequired().HasDefaultValueSql("now()");

            // Unique idempotency key — duplicate INSERTs from a producer
            // retry collide here and become no-ops.
            e.HasIndex(x => x.IdempotencyKey).IsUnique().HasDatabaseName($"ux_{tableName}_idempotency_key");

            // Partial index on (available_at) WHERE claimed_by IS NULL —
            // the hot path for ClaimAsync; SKIP LOCKED scans this and
            // never reads claimed rows.
            e.HasIndex(x => x.AvailableAt)
                .HasFilter("\"ClaimedBy\" IS NULL")
                .HasDatabaseName($"ix_{tableName}_available_unclaimed");

            // Lookup by work item — for cross-queue debugging and the
            // "what's in flight for this case?" admin view.
            e.HasIndex(x => x.WorkItemId).HasDatabaseName($"ix_{tableName}_work_item_id");

            // Janitor's lease-expiry sweep filters on (claimed_until)
            // for non-NULL claimed rows; partial index makes it cheap.
            e.HasIndex(x => x.ClaimedUntil)
                .HasFilter("\"ClaimedBy\" IS NOT NULL")
                .HasDatabaseName($"ix_{tableName}_claimed_until");
        });
    }
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. Reads the
/// connection string from <c>NICKERP_PLATFORM_DB_CONNECTION</c> (the
/// shared platform DB env var) with a localhost fallback.
/// </summary>
public sealed class QueueingDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<QueueingDbContext>
{
    public QueueingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NICKERP_PLATFORM_DB_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=nickerp_platform;Username=postgres;Password=designtime";

        var options = new DbContextOptionsBuilder<QueueingDbContext>()
            .UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(QueueingDbContext).Assembly.GetName().Name);
                // Match the convention used by the other platform schemas:
                // history table inside the owning schema so nscim_app
                // never needs CREATE on `public`.
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", QueueingDbContext.SchemaName);
            })
            .Options;

        return new QueueingDbContext(options);
    }
}
