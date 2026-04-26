using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NickERP.Platform.Tenancy.Entities;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// EF Core <see cref="ISaveChangesInterceptor"/> that stamps
/// <see cref="ITenantOwned.TenantId"/> on every newly-added entity from the
/// current <see cref="ITenantContext"/>. Modules register this interceptor
/// on their <c>DbContext</c> via
/// <c>options.AddInterceptors(scope.ServiceProvider.GetRequiredService&lt;TenantOwnedEntityInterceptor&gt;())</c>.
/// </summary>
/// <remarks>
/// Stamps only <c>EntityState.Added</c>. Updates leave <c>TenantId</c>
/// alone so a buggy module can't accidentally re-tenant existing rows. If
/// <see cref="ITenantContext.IsResolved"/> is false, throws — modules MUST
/// authenticate + resolve a tenant before saving.
/// </remarks>
public sealed class TenantOwnedEntityInterceptor : SaveChangesInterceptor
{
    private readonly ITenantContext _tenant;

    public TenantOwnedEntityInterceptor(ITenantContext tenant)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        StampTenant(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StampTenant(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampTenant(DbContext? context)
    {
        if (context is null) return;

        var added = context.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added && e.Entity is ITenantOwned)
            .ToList();

        if (added.Count == 0) return;

        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                $"Cannot save {added.Count} tenant-owned entit{(added.Count == 1 ? "y" : "ies")}: ITenantContext is not resolved. "
                + "Either call UseNickErpTenancy() in the request pipeline so the middleware sets the tenant from the JWT, "
                + "or explicitly call ITenantContext.SetTenant(...) for background jobs that impersonate a tenant.");
        }

        var tenantId = _tenant.TenantId;
        foreach (var entry in added)
        {
            var entity = (ITenantOwned)entry.Entity;
            // Only stamp if zero — preserves explicit overrides from admin tooling that needs to write across tenants.
            if (entity.TenantId == 0L)
            {
                entity.TenantId = tenantId;
            }
        }
    }
}
