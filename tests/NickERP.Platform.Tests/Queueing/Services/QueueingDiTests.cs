using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NickERP.Platform.Queueing;
using NickERP.Platform.Queueing.Abstractions;
using NickERP.Platform.Queueing.Services;
using NickERP.Platform.Tenancy;
using Npgsql;

namespace NickERP.Platform.Tests.Queueing.Services;

public sealed class QueueingDiTests
{
    private sealed record TestPayload(string Marker);
    private sealed record OtherPayload(string Marker);

    private sealed class TestConsumer : IQueueConsumer<TestPayload>
    {
        public Task ProcessAsync(IQueueClaim<TestPayload> claim, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OtherConsumer : IQueueConsumer<OtherPayload>
    {
        public Task ProcessAsync(IQueueClaim<OtherPayload> claim, CancellationToken ct) => Task.CompletedTask;
    }

    private static void AddTestDataSource(IServiceCollection services)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(
            "Host=localhost;Username=nickerp_test;Password=nickerp_test;Database=nickerp_test"));
    }

    [Fact]
    public void AddNickErpQueueing_RegistersTenantActivator_ThatPushesScopedTenant()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNickErpTenancy();
        AddTestDataSource(services);
        services.AddNickErpQueueing();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenantContext.IsResolved.Should().BeFalse();

        var activator = provider.GetRequiredService<ITenantContextActivator>();
        using (activator.PushTenant(scope.ServiceProvider, 42))
        {
            tenantContext.IsResolved.Should().BeTrue();
            tenantContext.IsSystem.Should().BeFalse();
            tenantContext.TenantId.Should().Be(42);
        }
    }

    [Fact]
    public void QueueingHostComposition_ValidatesJanitorAndConsumerHostedServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNickErpTenancy();
        AddTestDataSource(services);

        services.AddNickErpQueueing();
        services.AddPostgresQueue<TestPayload>(opts =>
        {
            opts.Schema = "inspection";
            opts.Name = "di_probe";
        });
        services.AddQueueConsumer<TestConsumer, TestPayload>();
        services.AddPostgresQueue<OtherPayload>(opts =>
        {
            opts.Schema = "inspection";
            opts.Name = "di_probe_other";
        });
        services.AddQueueConsumer<OtherConsumer, OtherPayload>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var hostedServiceNames = provider.GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .ToArray();

        hostedServiceNames.Should()
            .Contain(name => name.StartsWith("QueueConsumerHost", StringComparison.Ordinal));
        hostedServiceNames.Count(name => name.StartsWith("QueueConsumerHost", StringComparison.Ordinal))
            .Should()
            .Be(2);

        provider.GetRequiredService<ITransactionalQueue<TestPayload>>()
            .Should()
            .NotBeNull("state-machine producers need the transactional enqueue surface");

        provider.GetRequiredService<IQueue<TestPayload>>()
            .NotifyChannel
            .Should()
            .Be("queue_inspection_di_probe", "consumer hosts must listen on the same schema-qualified channel used by PostgresQueue");
    }
}
