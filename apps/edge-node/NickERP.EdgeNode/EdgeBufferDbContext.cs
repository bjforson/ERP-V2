using Microsoft.EntityFrameworkCore;

namespace NickERP.EdgeNode;

/// <summary>
/// Sprint 11 / P2 — EF Core context for the edge node's local SQLite
/// write-buffer. Single table: <c>edge_outbox</c>.
///
/// <para>
/// SQLite (not Postgres) because the edge runs in environments where
/// the server is intermittently unreachable; bundling a local
/// Postgres-server is far too heavyweight for a small inspection lane
/// or branch box. SQLite ships in-process, zero external dependency.
/// </para>
///
/// <para>
/// Migrations are not applied at runtime via <c>Database.Migrate()</c>
/// — the spec opts for a one-shot DDL script captured via
/// <c>dotnet ef migrations script --idempotent</c> at build time, and
/// the edge initialises its DB from that script on first boot. This
/// keeps the runtime closed-loop with no design-time tooling on the
/// edge box.
/// </para>
/// </summary>
public sealed class EdgeBufferDbContext : DbContext
{
    /// <summary>SQLite table name; lowercase per Postgres convention so server-side clones don't surprise.</summary>
    public const string TableName = "edge_outbox";

    public EdgeBufferDbContext(DbContextOptions<EdgeBufferDbContext> options) : base(options) { }

    /// <summary>
    /// Local pending-replay rows. The <see cref="EdgeReplayWorker"/>
    /// reads <c>WHERE ReplayedAt IS NULL ORDER BY Id ASC</c>; the
    /// <see cref="IEdgeEventCapture"/> impl writes one row per captured
    /// event.
    /// </summary>
    public DbSet<EdgeOutboxEntry> Outbox => Set<EdgeOutboxEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EdgeOutboxEntry>(e =>
        {
            e.ToTable(TableName);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.EventPayloadJson).IsRequired();
            e.Property(x => x.EventTypeHint).IsRequired().HasMaxLength(200);
            e.Property(x => x.EdgeTimestamp).IsRequired();
            e.Property(x => x.EdgeNodeId).IsRequired().HasMaxLength(100);
            e.Property(x => x.TenantId).IsRequired();
            e.Property(x => x.ReplayedAt);
            e.Property(x => x.ReplayAttempts).IsRequired().HasDefaultValue(0);
            e.Property(x => x.LastReplayError).HasMaxLength(2000);

            // Hot-path FIFO drain index: the worker scans
            // unreplayed rows in id-order on every tick. Partial index
            // on ReplayedAt IS NULL keeps it small as the queue fills
            // and drains.
            e.HasIndex(x => x.Id)
                .HasDatabaseName("ix_edge_outbox_pending")
                .HasFilter("\"ReplayedAt\" IS NULL");
        });
    }
}

/// <summary>Design-time factory; reads <c>EDGENODE_DB_PATH</c> or falls back to a local file.</summary>
public sealed class EdgeBufferDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<EdgeBufferDbContext>
{
    public EdgeBufferDbContext CreateDbContext(string[] args)
    {
        var dbPath = Environment.GetEnvironmentVariable("EDGENODE_DB_PATH")
            ?? "edge-outbox.db";
        var options = new DbContextOptionsBuilder<EdgeBufferDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new EdgeBufferDbContext(options);
    }
}
