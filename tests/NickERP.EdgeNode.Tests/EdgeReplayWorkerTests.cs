using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NickERP.EdgeNode;

namespace NickERP.EdgeNode.Tests;

/// <summary>
/// Sprint 11 / P2 — exercises <see cref="EdgeReplayWorker.TickAsync"/>
/// against the SQLite buffer + a mocked <see cref="IEdgeReplayClient"/>.
///
/// <para>
/// The worker drains the outbox FIFO, marks ReplayedAt on success,
/// stamps LastReplayError on a 4xx (without setting ReplayedAt), and
/// silently skips the tick when the server is unreachable. Each test
/// drives <c>TickAsync</c> directly; the BackgroundService loop is
/// not in scope here (it's a thin wrapper around TickAsync + a delay).
/// </para>
/// </summary>
public sealed class EdgeReplayWorkerTests : IAsyncLifetime
{
    private SqliteEdgeBufferFixture _fx = null!;

    public async Task InitializeAsync() => _fx = await SqliteEdgeBufferFixture.CreateAsync();
    public async Task DisposeAsync() => await _fx.DisposeAsync();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_drains_pending_rows_FIFO_when_server_reachable()
    {
        await Seed(_fx.Db, count: 3);

        var client = new ScriptedReplayClient(serverReachable: true);
        client.ScriptResponses(
            results: Enumerable.Range(0, 3).Select(_ => new EdgeReplayResult(true, null)).ToList());

        var worker = BuildWorker(_fx, client);
        await worker.TickAsync(CancellationToken.None);

        var rows = await _fx.Db.Outbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
        rows.Should().OnlyContain(r => r.ReplayedAt != null);
        rows.Should().OnlyContain(r => r.ReplayAttempts == 1);
        // Order observed by the client matches FIFO.
        client.ObservedBatchIds.Single().Should().Equal(rows.Select(r => r.Id));
        worker.QueueDepth.Should().Be(0);
        worker.LastSuccessfulReplayAt.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_skips_silently_when_server_unreachable()
    {
        await Seed(_fx.Db, count: 2);
        var client = new ScriptedReplayClient(serverReachable: false);
        var worker = BuildWorker(_fx, client);

        await worker.TickAsync(CancellationToken.None);

        var rows = await _fx.Db.Outbox.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.ReplayedAt == null);
        rows.Should().OnlyContain(r => r.ReplayAttempts == 0);
        client.SendBatchCalled.Should().BeFalse();
        worker.QueueDepth.Should().Be(2);
        worker.LastSuccessfulReplayAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_4xx_per_entry_marks_LastReplayError_but_not_ReplayedAt()
    {
        await Seed(_fx.Db, count: 2);
        var client = new ScriptedReplayClient(serverReachable: true);
        client.ScriptResponses(new List<EdgeReplayResult>
        {
            new(true, null),
            new(false, "tenant 99 not authorized for edge edge-test-1")
        });

        var worker = BuildWorker(_fx, client);
        await worker.TickAsync(CancellationToken.None);

        var rows = await _fx.Db.Outbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
        rows[0].ReplayedAt.Should().NotBeNull();
        rows[0].LastReplayError.Should().BeNull();
        rows[1].ReplayedAt.Should().BeNull();
        rows[1].LastReplayError.Should().Contain("not authorized");
        rows[1].ReplayAttempts.Should().Be(1);
        worker.QueueDepth.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_transient_5xx_bumps_attempts_only()
    {
        await Seed(_fx.Db, count: 2);
        var client = new ScriptedReplayClient(serverReachable: true);
        client.ThrowOnSend = new HttpRequestException("Server returned 503 (transient).");

        var worker = BuildWorker(_fx, client);
        await worker.TickAsync(CancellationToken.None);

        var rows = await _fx.Db.Outbox.AsNoTracking().ToListAsync();
        rows.Should().OnlyContain(r => r.ReplayedAt == null);
        rows.Should().OnlyContain(r => r.ReplayAttempts == 1);
        rows.Should().OnlyContain(r => r.LastReplayError == null);
        worker.LastSuccessfulReplayAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_rejected_event_stays_in_queue_and_eventually_succeeds_on_retry()
    {
        await Seed(_fx.Db, count: 1);
        var client = new ScriptedReplayClient(serverReachable: true);
        client.ScriptResponses(new List<EdgeReplayResult> { new(false, "transient downstream") });

        var worker = BuildWorker(_fx, client);
        await worker.TickAsync(CancellationToken.None);

        var row = await _fx.Db.Outbox.AsNoTracking().SingleAsync();
        row.ReplayedAt.Should().BeNull();
        row.ReplayAttempts.Should().Be(1);

        // Retry — server now succeeds.
        client.ScriptResponses(new List<EdgeReplayResult> { new(true, null) });
        await worker.TickAsync(CancellationToken.None);

        row = await _fx.Db.Outbox.AsNoTracking().SingleAsync();
        row.ReplayedAt.Should().NotBeNull();
        row.ReplayAttempts.Should().Be(2);
        // Last error retained for triage, even though the row succeeded.
        row.LastReplayError.Should().Be("transient downstream");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_logs_alert_when_attempts_cross_threshold_but_keeps_retrying()
    {
        // The spec says "after 5 attempts, raise an alert (log at error
        // level) but keep retrying — don't drop events." We simulate
        // five rejections, then verify on the sixth tick the row is
        // still present and ReplayAttempts has incremented past the
        // threshold.
        await Seed(_fx.Db, count: 1);
        var client = new ScriptedReplayClient(serverReachable: true);
        var worker = BuildWorker(_fx, client);

        for (var i = 0; i < EdgeReplayWorker.FailureAlertThreshold + 1; i++)
        {
            client.ScriptResponses(new List<EdgeReplayResult> { new(false, "still failing") });
            await worker.TickAsync(CancellationToken.None);
        }

        var row = await _fx.Db.Outbox.AsNoTracking().SingleAsync();
        row.ReplayedAt.Should().BeNull();
        row.ReplayAttempts.Should().Be(EdgeReplayWorker.FailureAlertThreshold + 1);
        row.LastReplayError.Should().Be("still failing");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_skips_probe_when_queue_is_empty()
    {
        var client = new ScriptedReplayClient(serverReachable: true);
        var worker = BuildWorker(_fx, client);

        await worker.TickAsync(CancellationToken.None);

        client.ProbeCalled.Should().BeFalse(
            "an empty queue should not bother the server with a reachability probe.");
        client.SendBatchCalled.Should().BeFalse();
        worker.QueueDepth.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Tick_respects_MaxBatchSize()
    {
        await Seed(_fx.Db, count: 10);
        var client = new ScriptedReplayClient(serverReachable: true);
        client.ScriptResponses(Enumerable.Range(0, 3).Select(_ => new EdgeReplayResult(true, null)).ToList());

        var worker = BuildWorker(_fx, client, maxBatchSize: 3);
        await worker.TickAsync(CancellationToken.None);

        var pending = await _fx.Db.Outbox.AsNoTracking()
            .Where(o => o.ReplayedAt == null).CountAsync();
        pending.Should().Be(7);
        worker.QueueDepth.Should().Be(7);
        client.ObservedBatchIds.Single().Should().HaveCount(3);
    }

    // ----- helpers ------------------------------------------------------

    private static async Task Seed(EdgeBufferDbContext db, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.Outbox.Add(new EdgeOutboxEntry
            {
                EventPayloadJson = JsonSerializer.Serialize(new
                {
                    idx = i,
                    eventType = "test.evt",
                    entityType = "T",
                    entityId = i.ToString()
                }),
                EventTypeHint = "audit.event.replay",
                EdgeTimestamp = new DateTimeOffset(2026, 4, 29, 9, 0, 0, TimeSpan.Zero).AddSeconds(i),
                EdgeNodeId = "edge-test-1",
                TenantId = 1,
                ReplayedAt = null,
                ReplayAttempts = 0
            });
        }
        await db.SaveChangesAsync();
    }

    private static EdgeReplayWorker BuildWorker(
        SqliteEdgeBufferFixture fx,
        IEdgeReplayClient client,
        int maxBatchSize = 50)
    {
        var services = new ServiceCollection();
        services.AddSingleton(fx.Db);
        services.AddSingleton(client);
        // Plain ServiceCollection -> ServiceProvider so the worker's
        // CreateAsyncScope() resolves the same DbContext instance the
        // test wired in.
        var sp = services.BuildServiceProvider();
        var scopeFactory = new SingleScopeFactory(sp);

        var opts = Options.Create(new EdgeNodeOptions
        {
            Id = "edge-test-1",
            Token = "test-token",
            ReplayIntervalSeconds = 30,
            MaxBatchSize = maxBatchSize
        });

        return new EdgeReplayWorker(scopeFactory, opts, NullLogger<EdgeReplayWorker>.Instance);
    }

    private sealed class SingleScopeFactory : IServiceScopeFactory
    {
        private readonly IServiceProvider _sp;
        public SingleScopeFactory(IServiceProvider sp) => _sp = sp;
        public IServiceScope CreateScope() => new Scope(_sp);

        private sealed class Scope : IServiceScope
        {
            public Scope(IServiceProvider sp) => ServiceProvider = sp;
            public IServiceProvider ServiceProvider { get; }
            public void Dispose() { /* shared root SP — nothing to dispose per scope */ }
        }
    }
}

/// <summary>
/// Test double for <see cref="IEdgeReplayClient"/>. Each call to
/// <see cref="ScriptResponses"/> queues the response the next batch
/// will receive; <see cref="ThrowOnSend"/> simulates transient
/// transport failures.
/// </summary>
internal sealed class ScriptedReplayClient : IEdgeReplayClient
{
    private readonly bool _serverReachable;
    private readonly Queue<IReadOnlyList<EdgeReplayResult>> _responses = new();

    public ScriptedReplayClient(bool serverReachable) => _serverReachable = serverReachable;

    /// <summary>List-of-id-list — batches observed across calls. Order matters.</summary>
    public List<List<long>> ObservedBatchIds { get; } = new();

    public bool ProbeCalled { get; private set; }
    public bool SendBatchCalled { get; private set; }
    public Exception? ThrowOnSend { get; set; }

    public void ScriptResponses(IReadOnlyList<EdgeReplayResult> results)
        => _responses.Enqueue(results);

    public Task<bool> IsServerReachableAsync(CancellationToken ct)
    {
        ProbeCalled = true;
        return Task.FromResult(_serverReachable);
    }

    public Task<EdgeReplayResponse> SendBatchAsync(
        IReadOnlyList<EdgeOutboxEntry> batch, CancellationToken ct)
    {
        SendBatchCalled = true;
        ObservedBatchIds.Add(batch.Select(b => b.Id).ToList());

        if (ThrowOnSend is not null)
            throw ThrowOnSend;

        if (_responses.Count == 0)
        {
            return Task.FromResult(new EdgeReplayResponse(
                batch.Select(_ => new EdgeReplayResult(true, null)).ToList()));
        }
        return Task.FromResult(new EdgeReplayResponse(_responses.Dequeue()));
    }
}
