using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Queueing.Abstractions;

namespace NickERP.Platform.Queueing.Services;

/// <summary>
/// Generic <see cref="BackgroundService"/> that wires an
/// <see cref="IQueueConsumer{TPayload}"/> to a <see cref="IQueue{TPayload}"/>:
/// LISTENs for wakeups, claims rows, dispatches to the consumer,
/// completes / fails the claim, and falls back to polling if no NOTIFY
/// arrives within the configured interval.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime.</b> One instance per consumer per app process. The host
/// is stateless beyond the inbound NOTIFY signal — multiple instances
/// of the same host run side by side and pull from the same queue
/// table without coordination beyond <c>SKIP LOCKED</c>.
/// </para>
/// <para>
/// <b>Scoped consumer resolution.</b> The consumer is resolved per row
/// from a fresh <see cref="IServiceScope"/> so it can take scoped
/// dependencies (DbContext, ITenantContext) without leaking state
/// between rows.
/// </para>
/// <para>
/// <b>Tenant context for background work.</b> Background workers
/// operate outside an HTTP scope, so the host doesn't have a tenant
/// pre-resolved. The claim's <see cref="IQueueClaim{TPayload}.TenantId"/>
/// is the authoritative answer — the host pushes it onto
/// <c>ITenantContext</c> for the duration of the dispatch via the
/// <see cref="ITenantContextActivator"/> hook so RLS-enforced reads in
/// the consumer see the right tenant.
/// </para>
/// </remarks>
/// <typeparam name="TPayload">Payload type for the queue this host services.</typeparam>
public sealed class QueueConsumerHost<TPayload> : BackgroundService
    where TPayload : class
{
    private readonly IQueue<TPayload> _queue;
    private readonly IPgNotifyListener _notifyListener;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantContextActivator _tenantActivator;
    private readonly QueueConsumerHostOptions _options;
    private readonly ILogger<QueueConsumerHost<TPayload>> _logger;
    private readonly SemaphoreSlim _wakeup = new(0, 1);

    public QueueConsumerHost(
        IQueue<TPayload> queue,
        IPgNotifyListener notifyListener,
        IServiceScopeFactory scopeFactory,
        ITenantContextActivator tenantActivator,
        QueueConsumerHostOptions options,
        ILogger<QueueConsumerHost<TPayload>> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _notifyListener = notifyListener ?? throw new ArgumentNullException(nameof(notifyListener));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _tenantActivator = tenantActivator ?? throw new ArgumentNullException(nameof(tenantActivator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerId = $"{Environment.MachineName}/{Environment.ProcessId}/{_queue.Name}";

        await using var subscription = await _notifyListener.SubscribeAsync(
            ResolveNotifyChannel(_queue.Name),
            (_, _) =>
            {
                if (_wakeup.CurrentCount == 0)
                {
                    try { _wakeup.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
                }
                return Task.CompletedTask;
            },
            stoppingToken).ConfigureAwait(false);

        _logger.LogInformation(
            "QueueConsumerHost<{Payload}> for queue {Queue} starting (worker id: {WorkerId})",
            typeof(TPayload).Name, _queue.Name, workerId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claim = await _queue.ClaimAsync(workerId, _options.LeaseDuration, stoppingToken).ConfigureAwait(false);
                if (claim is null)
                {
                    // Nothing ready — wait for either a wakeup or the
                    // poll interval, whichever comes first.
                    var winner = await Task.WhenAny(
                        _wakeup.WaitAsync(stoppingToken),
                        Task.Delay(_options.PollInterval, stoppingToken)).ConfigureAwait(false);
                    _ = winner;
                    continue;
                }

                await DispatchAsync(claim, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "QueueConsumerHost<{Payload}> top-level exception; backing off",
                    typeof(TPayload).Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation(
            "QueueConsumerHost<{Payload}> for queue {Queue} stopped",
            typeof(TPayload).Name, _queue.Name);
    }

    private async Task DispatchAsync(IQueueClaim<TPayload> claim, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var consumer = scope.ServiceProvider.GetRequiredService<IQueueConsumer<TPayload>>();

        // Push the claim's tenant onto the scoped tenant context so RLS
        // sees the right tenant on every connection the consumer opens.
        using var tenantScope = _tenantActivator.PushTenant(scope.ServiceProvider, claim.TenantId);

        try
        {
            await consumer.ProcessAsync(claim, ct).ConfigureAwait(false);
            await claim.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — let the lease expire so the row gets retried
            // by whoever picks up after restart. Don't fail the claim
            // explicitly; that would burn an attempt.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Consumer for queue {Queue} threw on row {RowId} (attempt {Attempt})",
                _queue.Name, claim.Id, claim.AttemptCount);
            await claim.FailAsync(ex, retryAfter: null, ct).ConfigureAwait(false);
        }
    }

    private static string ResolveNotifyChannel(string queueName)
    {
        // Conventional channel naming — must match
        // PostgresQueueOptions.NotifyChannel. Schema is captured implicitly
        // via the queue registration; this host is constructed once per
        // (schema,name) pair so we can't ambiguously route.
        return $"queue_{queueName}";
    }
}

/// <summary>Configuration for <see cref="QueueConsumerHost{TPayload}"/>.</summary>
public sealed class QueueConsumerHostOptions
{
    /// <summary>How long each claim's lease is valid. Default 5 minutes.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Polling fallback interval — used when LISTEN/NOTIFY hasn't fired. Default 5 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Hook the platform tenancy layer implements so background hosts can
/// push a tenant id into scope without taking a hard dependency on
/// <c>ITenantContext</c>'s mutable surface from this assembly.
/// </summary>
public interface ITenantContextActivator
{
    /// <summary>
    /// Push <paramref name="tenantId"/> onto the scope's
    /// <c>ITenantContext</c>. The returned <see cref="IDisposable"/>
    /// pops it on dispose.
    /// </summary>
    IDisposable PushTenant(IServiceProvider scopedProvider, long tenantId);
}
