using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Queueing.Services;
using Npgsql;

namespace NickERP.Platform.Tests.Queueing.Services;

/// <summary>
/// Sprint 14 / B-queues — Postgres integration tests for
/// <see cref="PostgresQueue{TPayload}"/>. Exercises the production
/// claim/lease/DLQ paths against a real Postgres 17 instance via
/// Testcontainers. Skips silently when Docker isn't available so CI
/// hosts without it still pass.
/// </summary>
[Collection("PostgresQueueIntegration")]
[Trait("Category", "Integration")]
public sealed class PostgresQueueIntegrationTests
{
    private readonly PostgresQueueIntegrationFixture _fx;

    public PostgresQueueIntegrationTests(PostgresQueueIntegrationFixture fx)
    {
        _fx = fx ?? throw new ArgumentNullException(nameof(fx));
    }

    private sealed record TestPayload(string Marker, int Number);

    private (PostgresQueue<TestPayload> queue, PostgresQueueOptions options) NewQueue(int maxAttempts = 5)
    {
        var options = new PostgresQueueOptions
        {
            Name = _fx.TestQueueName,
            Schema = _fx.TestQueueSchema,
            MaxAttempts = maxAttempts,
            DefaultLeaseDuration = TimeSpan.FromMinutes(5),
            // Tight backoff so tests don't sit waiting.
            RetryBackoff = new[]
            {
                TimeSpan.FromMilliseconds(50),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(200),
                TimeSpan.FromMilliseconds(400),
                TimeSpan.FromMilliseconds(800),
            }
        };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var queue = new PostgresQueue<TestPayload>(_fx.DataSource!, options, json, NullLogger<PostgresQueue<TestPayload>>.Instance);
        return (queue, options);
    }

    [Fact]
    public async Task Enqueue_Then_Claim_RoundTrip()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        var (queue, _) = NewQueue();
        var workItemId = Guid.NewGuid();

        var id = await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
        {
            WorkItemId = workItemId,
            Payload = new TestPayload("hello", 42),
            IdempotencyKey = "ipk-" + Guid.NewGuid().ToString("N"),
            CorrelationId = "corr-1"
        });

        id.Should().BeGreaterThan(0);

        await using var claim = await queue.ClaimAsync("worker-A", TimeSpan.FromMinutes(1));
        claim.Should().NotBeNull();
        claim!.WorkItemId.Should().Be(workItemId);
        claim.Payload.Marker.Should().Be("hello");
        claim.Payload.Number.Should().Be(42);
        claim.AttemptCount.Should().Be(1, "ClaimAsync increments attempt_count atomically");
        claim.CorrelationId.Should().Be("corr-1");

        await claim.CompleteAsync();
    }

    [Fact]
    public async Task Enqueue_DuplicateIdempotencyKey_DedupsAndReturnsSameId()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        var (queue, _) = NewQueue();
        var key = "ipk-dedupe-" + Guid.NewGuid().ToString("N");
        var workItemId = Guid.NewGuid();

        var id1 = await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
        {
            WorkItemId = workItemId,
            Payload = new TestPayload("first", 1),
            IdempotencyKey = key
        });

        var id2 = await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
        {
            WorkItemId = workItemId,
            Payload = new TestPayload("second", 2),
            IdempotencyKey = key
        });

        id2.Should().Be(id1, "ON CONFLICT DO NOTHING + re-SELECT returns the original row's id");

        await using var conn = await _fx.OpenScopedConnectionAsync();
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {_fx.TestQueueQualified} WHERE idempotency_key = @k", conn);
        cmd.Parameters.AddWithValue("k", key);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.Should().Be(1, "the duplicate INSERT was rejected; only one row exists");
    }

    [Fact]
    public async Task Claim_TwoWorkers_OnlyOneWins()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        var (queue, _) = NewQueue();
        await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
        {
            WorkItemId = Guid.NewGuid(),
            Payload = new TestPayload("contend", 1),
            IdempotencyKey = "ipk-" + Guid.NewGuid().ToString("N")
        });

        var taskA = Task.Run(() => queue.ClaimAsync("worker-A", TimeSpan.FromMinutes(1)));
        var taskB = Task.Run(() => queue.ClaimAsync("worker-B", TimeSpan.FromMinutes(1)));

        var claims = await Task.WhenAll(taskA, taskB);

        var nonNull = claims.Where(c => c is not null).ToList();
        nonNull.Should().HaveCount(1, "FOR UPDATE SKIP LOCKED guarantees exactly one winner");

        // Dispose both — the loser's null-cast guard is implicit.
        foreach (var c in claims) { if (c is not null) await c.CompleteAsync(); }
    }

    [Fact]
    public async Task LeaseExpired_QueueJanitorReleases()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        var (queue, _) = NewQueue();
        await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
        {
            WorkItemId = Guid.NewGuid(),
            Payload = new TestPayload("leaseexp", 1),
            IdempotencyKey = "ipk-" + Guid.NewGuid().ToString("N")
        });

        // Short lease so the janitor's sweep can find an expired row.
        var firstClaim = await queue.ClaimAsync("worker-stale", TimeSpan.FromMilliseconds(200));
        firstClaim.Should().NotBeNull();
        firstClaim!.AttemptCount.Should().Be(1);

        await Task.Delay(500);

        // Run the janitor manually for the test queue (mirrors what
        // QueueJanitor.SweepAsync does in production).
        await using (var conn = await _fx.OpenScopedConnectionAsync())
        await using (var cmd = new NpgsqlCommand(
            $@"UPDATE {_fx.TestQueueQualified}
               SET claimed_by = NULL, claimed_until = NULL
               WHERE claimed_by IS NOT NULL AND claimed_until < now();", conn))
        {
            var released = await cmd.ExecuteNonQueryAsync();
            released.Should().Be(1, "the expired claim was released");
        }

        var secondClaim = await queue.ClaimAsync("worker-fresh", TimeSpan.FromMinutes(1));
        secondClaim.Should().NotBeNull();
        secondClaim!.AttemptCount.Should().Be(2,
            "the row got re-claimed; attempt_count incremented on every ClaimAsync including reclaim");

        await secondClaim.CompleteAsync();
        // First claim's lease expired but we never resolved it; defer
        // disposal so the auto-fail path doesn't try to update a
        // completed/deleted row.
        if (firstClaim is IAsyncDisposable d) { await d.DisposeAsync(); }
    }

    [Fact]
    public async Task Notify_FiresOnEnqueue()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        var listener = new PgNotifyListener(_fx.DataSource!, NullLogger<PgNotifyListener>.Instance);
        using var cts = new CancellationTokenSource();
        var listenerTask = ((Microsoft.Extensions.Hosting.BackgroundService)listener).StartAsync(cts.Token);
        await listenerTask;

        var receivedPayloads = new List<string>();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var subscription = await listener.SubscribeAsync(
            _fx.TestQueueNotifyChannel,
            (payload, _) =>
            {
                lock (receivedPayloads) { receivedPayloads.Add(payload); }
                tcs.TrySetResult(payload);
                return Task.CompletedTask;
            });

        try
        {
            // Tiny pause so the listener's BackgroundService completes its
            // initial LISTEN issue before we emit. Without this the first
            // NOTIFY may race the LISTEN and silently drop.
            await Task.Delay(200);

            var (queue, _) = NewQueue();
            var workItemId = Guid.NewGuid();
            await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
            {
                WorkItemId = workItemId,
                Payload = new TestPayload("notif", 1),
                IdempotencyKey = "ipk-" + Guid.NewGuid().ToString("N")
            });

            var fired = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            fired.Should().BeSameAs(tcs.Task, "PgNotifyListener delivered the notification within 2s");
            var deliveredPayload = await tcs.Task;
            deliveredPayload.Should().Be(workItemId.ToString(),
                "PostgresQueue.NotifyAsync passes work_item_id as the NOTIFY payload");
        }
        finally
        {
            await subscription.DisposeAsync();
            cts.Cancel();
            try { await listener.StopAsync(CancellationToken.None); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task FailAsync_BeforeMaxAttempts_RequeuesWithBackoff()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        var (queue, _) = NewQueue(maxAttempts: 5);
        var rowId = await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
        {
            WorkItemId = Guid.NewGuid(),
            Payload = new TestPayload("retry", 1),
            IdempotencyKey = "ipk-" + Guid.NewGuid().ToString("N")
        });

        var claim = await queue.ClaimAsync("worker-A", TimeSpan.FromMinutes(1));
        claim.Should().NotBeNull();
        await claim!.FailAsync(new InvalidOperationException("transient blip"));

        await using var conn = await _fx.OpenScopedConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT claimed_by, available_at, last_error, attempt_count FROM {_fx.TestQueueQualified} WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", rowId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the row is still in the source queue");

        reader.IsDBNull(0).Should().BeTrue("FailAsync below MaxAttempts releases the lease");
        var availableAt = reader.GetFieldValue<DateTimeOffset>(1);
        availableAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMilliseconds(-50),
            "AvailableAt is shifted into the future by the backoff schedule");
        reader.GetString(2).Should().Be("transient blip");
        reader.GetInt32(3).Should().Be(1, "Fail keeps the attempt count from the failed claim");
    }

    [Fact]
    public async Task FailAsync_AtMaxAttempts_PromotesToDeadLetter()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        var (queue, options) = NewQueue(maxAttempts: 3);
        var rowId = await queue.EnqueueAsync(new EnqueueRequest<TestPayload>
        {
            WorkItemId = Guid.NewGuid(),
            Payload = new TestPayload("poison", 1),
            IdempotencyKey = "ipk-" + Guid.NewGuid().ToString("N")
        });

        // Fail 3 times — the 3rd promotes to DLQ.
        for (int i = 0; i < options.MaxAttempts; i++)
        {
            // Manually clear lease so the next ClaimAsync can reach the row;
            // tight-backoff schedule means we have to wait less than the
            // available_at delta. Easier to clear directly.
            await using (var conn = await _fx.OpenScopedConnectionAsync())
            await using (var reset = new NpgsqlCommand(
                $"UPDATE {_fx.TestQueueQualified} SET claimed_by=NULL, claimed_until=NULL, available_at=now() WHERE id=@id", conn))
            {
                reset.Parameters.AddWithValue("id", rowId);
                await reset.ExecuteNonQueryAsync();
            }

            var c = await queue.ClaimAsync($"worker-{i}", TimeSpan.FromMinutes(1));
            c.Should().NotBeNull($"row should still be claimable on attempt {i + 1}");
            await c!.FailAsync(new InvalidOperationException($"poison-attempt-{i + 1}"));
        }

        await using (var conn = await _fx.OpenScopedConnectionAsync())
        {
            await using (var liveCheck = new NpgsqlCommand(
                $"SELECT count(*) FROM {_fx.TestQueueQualified} WHERE id = @id", conn))
            {
                liveCheck.Parameters.AddWithValue("id", rowId);
                var live = (long)(await liveCheck.ExecuteScalarAsync())!;
                live.Should().Be(0, "the source-queue row is gone after promotion");
            }

            await using (var dlqCheck = new NpgsqlCommand(
                @"SELECT ""SourceQueue"", ""LastError"" FROM queueing.dead_letter
                  WHERE ""SourceQueueRowId"" = @id", conn))
            {
                dlqCheck.Parameters.AddWithValue("id", rowId);
                await using var reader = await dlqCheck.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue("a DLQ row was created");
                reader.GetString(0).Should().Be(
                    $"{options.Schema}.queue_{options.Name}",
                    "SourceQueue tags which queue the poison message came from");
                reader.GetString(1).Should().Contain("poison-attempt-3", "LastError captures the final failure message");
            }
        }
    }

    [Fact]
    public async Task Rls_TenantIsolation_PreventsCrossClaim()
    {
        if (!_fx.IsAvailable) return;
        await _fx.ResetAsync();

        // Step 1: enqueue a row scoped to tenant 1 by directly issuing the
        // INSERT under app.tenant_id=1 (PostgresQueue uses the data source
        // directly so the row picks up whatever app.tenant_id is set on
        // its connection). The platform's TenantConnectionInterceptor handles
        // this in-app; we replicate it here by setting it manually.
        var idempotencyKey = "ipk-" + Guid.NewGuid().ToString("N");
        await using (var connT1 = await _fx.OpenScopedConnectionAsync(tenantId: 1L))
        await using (var ins = new NpgsqlCommand(
            $@"INSERT INTO {_fx.TestQueueQualified}
                   (work_item_id, payload, idempotency_key)
               VALUES
                   (@wi, '{{}}'::jsonb, @ik)
               RETURNING id;", connT1))
        {
            ins.Parameters.AddWithValue("wi", Guid.NewGuid());
            ins.Parameters.AddWithValue("ik", idempotencyKey);
            var inserted = (long?)await ins.ExecuteScalarAsync();
            inserted.Should().NotBeNull();
        }

        // Step 2: under app.tenant_id=2, the row must be invisible.
        await using var connT2 = await _fx.OpenScopedConnectionAsync(tenantId: 2L);

        const string claimSql = @"
            WITH next AS (
                SELECT id FROM ""inspection"".""queue_test_queue""
                WHERE claimed_by IS NULL AND available_at <= now()
                ORDER BY available_at FOR UPDATE SKIP LOCKED LIMIT 1
            )
            UPDATE ""inspection"".""queue_test_queue"" q
            SET claimed_by = 'worker-T2', claimed_until = now() + interval '1 minute', attempt_count = q.attempt_count + 1
            FROM next WHERE q.id = next.id
            RETURNING q.id;";
        await using (var cmd = new NpgsqlCommand(claimSql, connT2))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeFalse(
                "RLS hides tenant 1's row from tenant 2's claim attempt");
        }

        // Sanity: the row IS visible to tenant 1.
        await using var connT1Verify = await _fx.OpenScopedConnectionAsync(tenantId: 1L);
        await using (var verify = new NpgsqlCommand(
            $"SELECT count(*) FROM {_fx.TestQueueQualified} WHERE idempotency_key = @ik", connT1Verify))
        {
            verify.Parameters.AddWithValue("ik", idempotencyKey);
            var visible = (long)(await verify.ExecuteScalarAsync())!;
            visible.Should().Be(1, "tenant 1 still sees its own row");
        }
    }
}
