using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NickERP.EdgeNode;

namespace NickERP.EdgeNode.Tests;

/// <summary>
/// Per-test fixture wrapping a fresh in-memory SQLite buffer. The
/// SQLite file lives only for the lifetime of the connection, so every
/// fixture instance starts with an empty <c>edge_outbox</c>.
///
/// <para>
/// We use real SQLite (not the EF in-memory provider) because the
/// outbox uses a partial index on <c>ReplayedAt IS NULL</c> which
/// the EF in-memory provider silently ignores; SQLite mirrors
/// production semantics.
/// </para>
/// </summary>
public sealed class SqliteEdgeBufferFixture : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public EdgeBufferDbContext Db { get; }

    private SqliteEdgeBufferFixture(SqliteConnection conn, EdgeBufferDbContext db)
    {
        _connection = conn;
        Db = db;
    }

    public static async Task<SqliteEdgeBufferFixture> CreateAsync()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        await conn.OpenAsync();
        var options = new DbContextOptionsBuilder<EdgeBufferDbContext>()
            .UseSqlite(conn)
            .Options;
        var db = new EdgeBufferDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new SqliteEdgeBufferFixture(conn, db);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// Trivial <see cref="TimeProvider"/> stub for deterministic-clock
/// tests. Holds a single instant; <see cref="Advance"/> bumps it
/// forward.
/// </summary>
public sealed class FixedClock : TimeProvider
{
    public FixedClock(DateTimeOffset now) => Now = now;
    public DateTimeOffset Now { get; private set; }
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan delta) => Now = Now.Add(delta);
}
