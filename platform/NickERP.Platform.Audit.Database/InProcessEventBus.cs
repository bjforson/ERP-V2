using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Audit.Events;

namespace NickERP.Platform.Audit.Database;

/// <summary>
/// In-process default <see cref="IEventBus"/>. Channel names map to prefix
/// strings (e.g. <c>nickerp</c> matches everything starting with
/// <c>nickerp.</c>). Each handler runs sequentially per event; one slow
/// or failing handler does not block delivery to other handlers (errors
/// are logged + swallowed — the event is already persisted).
/// </summary>
/// <remarks>
/// Singleton. Out-of-process delivery (Postgres LISTEN/NOTIFY, Redis pub/sub)
/// is a future swap-in: the implementation is decoupled behind <see cref="IEventBus"/>.
/// </remarks>
internal sealed class InProcessEventBus : IEventBus
{
    private readonly ILogger<InProcessEventBus> _logger;
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public InProcessEventBus(ILogger<InProcessEventBus> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PublishAsync(string channel, DomainEvent evt, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(evt);

        // Snapshot to avoid mutation during iteration.
        Subscription[] handlers;
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(channel, out var list) || list.Count == 0)
            {
                return;
            }
            handlers = list.ToArray();
        }

        foreach (var sub in handlers)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await sub.Handler(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Subscriber on channel {Channel} threw for event {EventType} {EventId}; subsequent subscribers still notified",
                    channel, evt.EventType, evt.EventId);
            }
        }
    }

    public IAsyncDisposable Subscribe(string channel, Func<DomainEvent, CancellationToken, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);

        var sub = new Subscription(channel, handler, this);
        lock (_lock)
        {
            var list = _subscriptions.GetOrAdd(channel, _ => new List<Subscription>());
            list.Add(sub);
        }
        return sub;
    }

    private void Detach(Subscription sub)
    {
        lock (_lock)
        {
            if (_subscriptions.TryGetValue(sub.Channel, out var list))
            {
                list.Remove(sub);
                if (list.Count == 0) _subscriptions.TryRemove(sub.Channel, out _);
            }
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        public string Channel { get; }
        public Func<DomainEvent, CancellationToken, Task> Handler { get; }
        private readonly InProcessEventBus _bus;
        private bool _disposed;

        public Subscription(string channel, Func<DomainEvent, CancellationToken, Task> handler, InProcessEventBus bus)
        {
            Channel = channel;
            Handler = handler;
            _bus = bus;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            _bus.Detach(this);
            return ValueTask.CompletedTask;
        }
    }
}
