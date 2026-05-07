using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Queueing.Entities;
using NickERP.Platform.Queueing.Services;
using Npgsql;

namespace NickERP.Platform.Tests.Queueing.Services;

/// <summary>
/// Sprint 14 / B-queues — outbox-relay integration test. Drives one
/// drain-loop tick of <see cref="OutboxRelay"/> against a real
/// Postgres + a fake adapter, asserting the row is dispatched and
/// then deleted.
/// </summary>
[Collection("PostgresQueueIntegration")]
[Trait("Category", "Integration")]
public sealed class OutboxRelayIntegrationTests
{
    private readonly PostgresQueueIntegrationFixture _fx;

    public OutboxRelayIntegrationTests(PostgresQueueIntegrationFixture fx)
    {
        _fx = fx ?? throw new ArgumentNullException(nameof(fx));
    }

    private sealed class RecordingAdapter : IOutboundAdapter
    {
        public string DestinationKind => "test";
        public List<QueueOutboxRow> Calls { get; } = new();

        public Task DispatchAsync(QueueOutboxRow row, CancellationToken ct)
        {
            Calls.Add(row);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OutboxRelay_DrainsRow_DispatchesToAdapter_AndDeletesRow()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        // Step 1: insert one queueing.outbox row directly via SQL. We
        // bypass IOutboxWriter — the relay's contract is "find a ready
        // row, dispatch, delete"; how the row got there is producer
        // concern.
        long rowId;
        var idempotencyKey = "ipk-outbox-" + Guid.NewGuid().ToString("N");
        var workItemId = Guid.NewGuid();

        await using (var conn = await _fx.OpenScopedConnectionAsync())
        await using (var cmd = new NpgsqlCommand(@"
            INSERT INTO queueing.outbox
                (""WorkItemId"", ""IdempotencyKey"", ""DestinationKind"", ""DestinationName"", ""Payload"", ""ContentType"")
            VALUES
                (@wi, @ik, 'test', 'fake-endpoint', '{""body"":""hello""}'::jsonb, 'application/json')
            RETURNING ""Id"";", conn))
        {
            cmd.Parameters.AddWithValue("wi", workItemId);
            cmd.Parameters.AddWithValue("ik", idempotencyKey);
            rowId = (long)(await cmd.ExecuteScalarAsync())!;
        }

        // Step 2: build the relay with a stub PgNotifyListener (no NOTIFY
        // hookup needed for the manual drain) and the recording adapter.
        var adapter = new RecordingAdapter();
        var listener = new StubPgNotifyListener();
        var options = new OutboxRelayOptions
        {
            WorkerId = "test-relay",
            LeaseDuration = TimeSpan.FromMinutes(1),
            PollInterval = TimeSpan.FromSeconds(5),
            BatchSize = 32
        };
        var relay = new OutboxRelay(
            _fx.DataSource!,
            listener,
            new IOutboundAdapter[] { adapter },
            options,
            NullLogger<OutboxRelay>.Instance);

        // Step 3: invoke the relay's drain loop for one tick. The DrainAsync
        // method is private, so we exercise the same effect by running the
        // BackgroundService briefly: start it, wait for one drain pass to
        // complete, stop.
        using var cts = new CancellationTokenSource();
        await ((BackgroundService)relay).StartAsync(cts.Token);

        // Poll until the adapter records the call (or timeout).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (adapter.Calls.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        // Stop the relay so its background loop doesn't keep running.
        cts.Cancel();
        try { await ((BackgroundService)relay).StopAsync(CancellationToken.None); }
        catch { /* best-effort */ }

        adapter.Calls.Should().HaveCount(1, "the relay dispatched the seeded row exactly once");
        var dispatched = adapter.Calls.Single();
        dispatched.WorkItemId.Should().Be(workItemId);
        dispatched.DestinationKind.Should().Be("test");
        dispatched.IdempotencyKey.Should().Be(idempotencyKey);

        // Step 4: confirm the row is gone from queueing.outbox.
        await using (var conn = await _fx.OpenScopedConnectionAsync())
        await using (var verify = new NpgsqlCommand(@"SELECT count(*) FROM queueing.outbox WHERE ""Id"" = @id;", conn))
        {
            verify.Parameters.AddWithValue("id", rowId);
            var remaining = (long)(await verify.ExecuteScalarAsync())!;
            remaining.Should().Be(0, "the relay deletes the row on adapter success");
        }
    }

    /// <summary>
    /// Minimal <see cref="IPgNotifyListener"/> stub for tests — the relay
    /// subscribes for low-latency wakeups but the test drives the drain
    /// via the polling fallback. Subscribe returns a no-op disposable;
    /// no notifications are ever delivered.
    /// </summary>
    private sealed class StubPgNotifyListener : IPgNotifyListener
    {
        public Task<IAsyncDisposable> SubscribeAsync(string channel, Func<string, CancellationToken, Task> handler, CancellationToken ct = default)
        {
            return Task.FromResult<IAsyncDisposable>(new NoopDisposable());
        }

        private sealed class NoopDisposable : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
