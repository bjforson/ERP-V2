using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Long-lived <see cref="BackgroundService"/> that holds a single Npgsql
/// connection in async LISTEN mode and dispatches incoming NOTIFY
/// payloads to in-process subscribers. One listener per app instance
/// multiplexes any number of channels so we don't run a connection per
/// queue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one connection.</b> Postgres LISTEN keeps a connection bound
/// to the listening session; you can't pool it. One multiplexed
/// connection scales to hundreds of channels without breaking sweat —
/// see Postgres docs on LISTEN/NOTIFY for the upper bounds.
/// </para>
/// <para>
/// <b>Reconnect.</b> On connection failure the listener loops with
/// exponential backoff (1s → 30s cap) and re-issues LISTEN for every
/// registered channel. NOTIFYs that arrive during the gap are missed —
/// that's why <c>QueueConsumerHost</c> always polls as a fallback (via
/// the queue's natural <c>available_at</c> ordering). NOTIFY is a
/// latency optimisation, never the source of correctness.
/// </para>
/// <para>
/// <b>Subscribers.</b> Register via <see cref="SubscribeAsync"/>; the returned
/// <see cref="IAsyncDisposable"/> detaches when disposed. Handlers run
/// synchronously on the listener's task — keep them short (signal a
/// <c>SemaphoreSlim</c>, push to a <c>Channel&lt;T&gt;</c>) so the
/// listener can drain its inbound queue.
/// </para>
/// </remarks>
public sealed class PgNotifyListener : BackgroundService, IPgNotifyListener
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PgNotifyListener> _logger;
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptions = new();
    private readonly SemaphoreSlim _subscriptionsLock = new(1, 1);

    public PgNotifyListener(NpgsqlDataSource dataSource, ILogger<PgNotifyListener> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<string, CancellationToken, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(this, channel, handler);
        await _subscriptionsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = _subscriptions.GetOrAdd(channel, _ => new List<Subscription>());
            list.Add(subscription);

            // If the listener is already running, issue a LISTEN for the
            // new channel on its connection. If it's not running yet,
            // ExecuteAsync will pick it up on first connect.
            if (_listenConnection is { } conn && !ct.IsCancellationRequested)
            {
                await IssueListenAsync(conn, channel, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _subscriptionsLock.Release();
        }

        _logger.LogDebug("Subscribed to LISTEN channel {Channel}", channel);
        return subscription;
    }

    private NpgsqlConnection? _listenConnection;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(stoppingToken).ConfigureAwait(false);
                _listenConnection = conn;

                conn.Notification += OnNotification;

                // Re-issue LISTEN for every currently-registered channel.
                foreach (var channel in _subscriptions.Keys)
                {
                    await IssueListenAsync(conn, channel, stoppingToken).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "PgNotifyListener connected; listening on {ChannelCount} channels",
                    _subscriptions.Count);

                // Reset backoff on successful connect.
                backoff = TimeSpan.FromSeconds(1);

                // Wait blocks until cancelled or the connection drops.
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "PgNotifyListener connection lost — reconnecting in {Backoff}",
                    backoff);
                _listenConnection = null;
                try
                {
                    await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, maxBackoff.TotalMilliseconds));
            }
        }

        _listenConnection = null;
        _logger.LogInformation("PgNotifyListener stopped");
    }

    private void OnNotification(object _, NpgsqlNotificationEventArgs args)
    {
        if (!_subscriptions.TryGetValue(args.Channel, out var subscribers))
        {
            return;
        }

        // Snapshot to a local array so a mid-iteration unsubscribe
        // doesn't break the loop (the event fires on the listener's
        // thread, subscribe/unsubscribe runs elsewhere).
        Subscription[] snapshot;
        lock (subscribers)
        {
            snapshot = subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            // Fire-and-forget — the handler is required to be cheap.
            // We don't await here because the listener's WaitAsync
            // would block behind a slow consumer otherwise.
            _ = SafeInvokeAsync(subscriber, args.Payload);
        }
    }

    private async Task SafeInvokeAsync(Subscription subscriber, string payload)
    {
        try
        {
            await subscriber.Handler(payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Notification handler threw on channel {Channel}",
                subscriber.Channel);
        }
    }

    private static async Task IssueListenAsync(NpgsqlConnection conn, string channel, CancellationToken ct)
    {
        // LISTEN takes an identifier, not a parameter — quote-escape via
        // a strict allow-list. Channel names in this codebase are
        // computed by PostgresQueueOptions.NotifyChannel and are
        // ASCII-only by construction, but defence in depth.
        if (!IsSafeChannelName(channel))
        {
            throw new ArgumentException(
                $"Channel name '{channel}' contains characters that aren't safe for LISTEN.",
                nameof(channel));
        }
        await using var cmd = new NpgsqlCommand($"LISTEN \"{channel}\";", conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static bool IsSafeChannelName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 63)
        {
            return false;
        }
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }
        return true;
    }

    internal void Detach(Subscription subscription)
    {
        if (_subscriptions.TryGetValue(subscription.Channel, out var list))
        {
            lock (list)
            {
                list.Remove(subscription);
            }
        }
    }

    /// <summary>
    /// Active subscription handle returned by <see cref="SubscribeAsync"/>.
    /// Disposing detaches the handler.
    /// </summary>
    internal sealed class Subscription : IAsyncDisposable
    {
        private readonly PgNotifyListener _owner;
        public string Channel { get; }
        public Func<string, CancellationToken, Task> Handler { get; }

        public Subscription(PgNotifyListener owner, string channel, Func<string, CancellationToken, Task> handler)
        {
            _owner = owner;
            Channel = channel;
            Handler = handler;
        }

        public ValueTask DisposeAsync()
        {
            _owner.Detach(this);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// In-process pub/sub primitive over Postgres LISTEN/NOTIFY. Used by
/// queue consumer hosts to wake up when a producer enqueues new work,
/// and by anyone else that needs cross-process signalling on the same
/// DB.
/// </summary>
public interface IPgNotifyListener
{
    /// <summary>
    /// Subscribe to a NOTIFY channel. The handler is invoked once per
    /// arriving notification; payloads are whatever the producer
    /// passed to <c>pg_notify(channel, payload)</c>.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(string channel, Func<string, CancellationToken, Task> handler, CancellationToken ct = default);
}
