using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NickERP.Platform.Core.Tenancy;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that automatically populates
/// the <see cref="ITenantOwned.TenantId"/> on every entity being inserted,
/// and validates that updates do not change the tenant id.
///
/// Modules wire this up by calling
/// <c>options.AddInterceptors(serviceProvider.GetRequiredService&lt;TenantOwnedEntityInterceptor&gt;())</c>
/// in their DbContext registration.
/// </summary>
public sealed class TenantOwnedEntityInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<TenantOwnedEntityInterceptor> _logger;

    public TenantOwnedEntityInterceptor(
        ITenantContext tenantContext,
        ILogger<TenantOwnedEntityInterceptor> logger)
    {
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyTenant(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ApplyTenant(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void ApplyTenant(DbContext? context)
    {
        if (context is null) return;

        var tenantId = _tenantContext.TenantId;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is not ITenantOwned tenantOwned) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    if (tenantOwned.TenantId == 0)
                    {
                        tenantOwned.TenantId = tenantId;
                    }
                    else if (tenantOwned.TenantId != tenantId && !_tenantContext.IsPlatformAdmin)
                    {
                        _logger.LogError(
                            "Tenant id mismatch on insert: entity {Entity} had {EntityTenant} but current context is {ContextTenant}",
                            entry.Metadata.Name, tenantOwned.TenantId, tenantId);
                        throw new InvalidOperationException(
                            $"Cannot insert entity {entry.Metadata.Name} with TenantId={tenantOwned.TenantId} from a context bound to TenantId={tenantId}.");
                    }
                    break;

                case EntityState.Modified:
                    var originalTenantId = (long)(entry.OriginalValues[nameof(ITenantOwned.TenantId)] ?? 0L);
                    if (originalTenantId != tenantOwned.TenantId && !_tenantContext.IsPlatformAdmin)
                    {
                        _logger.LogError(
                            "Tenant id change blocked on update: entity {Entity} attempted {Old} -> {New}",
                            entry.Metadata.Name, originalTenantId, tenantOwned.TenantId);
                        throw new InvalidOperationException(
                            $"TenantId on {entry.Metadata.Name} cannot be changed after creation.");
                    }
                    break;
            }
        }
    }
}
