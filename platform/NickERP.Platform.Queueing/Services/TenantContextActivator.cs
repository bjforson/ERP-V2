using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Tenancy;

namespace NickERP.Platform.Queueing.Services;

internal sealed class TenantContextActivator : ITenantContextActivator
{
    public IDisposable PushTenant(IServiceProvider scopedProvider, long tenantId)
    {
        ArgumentNullException.ThrowIfNull(scopedProvider);

        var tenantContext = scopedProvider.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);

        // QueueConsumerHost creates a fresh DI scope per claim, so disposing
        // the owning scope clears the mutable tenant context after dispatch.
        return NoopTenantScope.Instance;
    }

    private sealed class NoopTenantScope : IDisposable
    {
        public static readonly NoopTenantScope Instance = new();
        public void Dispose() { }
    }
}
