using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Queueing.Services;
using Npgsql;

namespace NickERP.Platform.Queueing;

/// <summary>
/// DI wiring entry points for the queueing platform. Modules call
/// <see cref="AddNickErpQueueing"/> once at startup, then
/// <see cref="AddPostgresQueue{TPayload}"/> + <see cref="AddQueueConsumer{TConsumer,TPayload}"/>
/// for each (queue, consumer) pair they own.
/// </summary>
public static class QueueingServiceCollectionExtensions
{
    /// <summary>
    /// Register the platform-wide queueing services: <see cref="PgNotifyListener"/>,
    /// <see cref="QueueJanitor"/>, <see cref="OutboxRelay"/>,
    /// <see cref="QueueMetricsRefresher"/>, plus the JSON serializer
    /// options used by all queues. Idempotent — safe to call multiple
    /// times (only the first call registers; subsequent calls are
    /// no-ops via <c>TryAddSingleton</c>).
    /// </summary>
    /// <param name="services">DI container to register into.</param>
    /// <param name="configureJson">
    /// Optional callback to customise the queue payload JSON serializer.
    /// Defaults are <c>JsonSerializerDefaults.Web</c> so payloads use
    /// camelCase and accept tolerant casing on read.
    /// </param>
    /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddNickErpQueueing(
        this IServiceCollection services,
        Action<JsonSerializerOptions>? configureJson = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        configureJson?.Invoke(json);
        services.TryAddSingleton(json);
        services.TryAddSingleton<ITenantContextActivator, TenantContextActivator>();

        // PgNotifyListener is both a singleton service AND a hosted
        // service. The cast lets us register it once and have it
        // resolve to the same instance from both surfaces.
        services.TryAddSingleton<PgNotifyListener>();
        services.TryAddSingleton<IPgNotifyListener>(sp => sp.GetRequiredService<PgNotifyListener>());
        services.AddHostedService(sp => sp.GetRequiredService<PgNotifyListener>());

        services.TryAddSingleton<QueueJanitorOptions>();
        services.AddHostedService<QueueJanitor>();

        services.TryAddSingleton<OutboxRelayOptions>();
        services.AddHostedService<OutboxRelay>();

        services.TryAddSingleton<QueueMetricsRefresherOptions>();
        services.AddHostedService<QueueMetricsRefresher>();

        return services;
    }

    /// <summary>
    /// Register a single Postgres-backed queue. The payload type
    /// <typeparamref name="TPayload"/> is the per-row consumer-specific
    /// shape; the queue's table is <c>{schema}.queue_{name}</c> as
    /// configured by <paramref name="configure"/>.
    /// </summary>
    /// <typeparam name="TPayload">Per-row consumer-specific data type. Must be a class for JSON deserialisation.</typeparam>
    /// <param name="services">DI container to register into.</param>
    /// <param name="configure">Mutate the <see cref="PostgresQueueOptions"/> instance for this queue.</param>
    /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddPostgresQueue<TPayload>(
        this IServiceCollection services,
        Action<PostgresQueueOptions> configure)
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PostgresQueueOptions();
        configure(options);
        options.Validate();

        // Track this queue's qualified table name so the janitor
        // includes it in its sweep. Aggregated by the janitor at
        // startup via the IPostQueueRegistration enumeration.
        services.AddSingleton<IPostQueueRegistration>(_ => new PostQueueRegistration(options));

        services.AddSingleton<IQueue<TPayload>>(sp => new PostgresQueue<TPayload>(
            sp.GetRequiredService<NpgsqlDataSource>(),
            options,
            sp.GetRequiredService<JsonSerializerOptions>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PostgresQueue<TPayload>>>()));

        services.AddSingleton<ITransactionalQueue<TPayload>>(sp => new TransactionalPostgresQueue<TPayload>(
            options,
            sp.GetRequiredService<JsonSerializerOptions>()));

        return services;
    }

    /// <summary>
    /// Register a queue consumer + its host. The consumer's
    /// <see cref="IQueueConsumer{TPayload}.ProcessAsync"/> runs once per
    /// claimed row; the host's loop handles claim management and the
    /// LISTEN/poll fallback.
    /// </summary>
    /// <typeparam name="TConsumer">Concrete consumer type — registered as scoped (one instance per row).</typeparam>
    /// <typeparam name="TPayload">Payload type — must match the queue's <c>TPayload</c>.</typeparam>
    /// <param name="services">DI container to register into.</param>
    /// <param name="configure">Optional configuration for the host (lease duration, poll interval).</param>
    /// <returns>The <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddQueueConsumer<TConsumer, TPayload>(
        this IServiceCollection services,
        Action<QueueConsumerHostOptions>? configure = null)
        where TConsumer : class, IQueueConsumer<TPayload>
        where TPayload : class
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new QueueConsumerHostOptions();
        configure?.Invoke(options);

        services.AddScoped<IQueueConsumer<TPayload>, TConsumer>();
        services.AddSingleton(options);
        services.AddHostedService<QueueConsumerHost<TPayload>>();

        return services;
    }

    /// <summary>
    /// Register an <see cref="IOutboundAdapter"/> with the platform.
    /// One registration per <see cref="IOutboundAdapter.DestinationKind"/>;
    /// duplicate kinds throw at startup so misconfiguration is caught
    /// before any rows are dispatched.
    /// </summary>
    public static IServiceCollection AddOutboundAdapter<TAdapter>(this IServiceCollection services)
        where TAdapter : class, IOutboundAdapter
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IOutboundAdapter, TAdapter>();
        return services;
    }
}

/// <summary>
/// Marker interface used by <see cref="QueueingServiceCollectionExtensions.AddPostgresQueue{TPayload}"/>
/// to publish a queue's qualified table name to the janitor's sweep
/// list at startup. Infrastructure-only — modules should not implement
/// this directly.
/// </summary>
public interface IPostQueueRegistration
{
    string QualifiedTableName { get; }
}

internal sealed class PostQueueRegistration : IPostQueueRegistration
{
    public PostQueueRegistration(PostgresQueueOptions options) => QualifiedTableName = options.QualifiedTableName;
    public string QualifiedTableName { get; }
}
