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

    /// <summary>
    /// Stamp <see cref="ITenantOwned.TenantId"/> on newly-added entities from
    /// the resolved tenant context. Throws when the context is unresolved so
    /// modules can't accidentally save tenant-owned data outside a request
    /// scope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sprint 5 (G1-3) — system-mode behaviour. When the caller has invoked
    /// <see cref="ITenantContext.SetSystemContext"/>, <see cref="ITenantContext.IsResolved"/>
    /// is <c>true</c> and <see cref="ITenantContext.TenantId"/> is the
    /// sentinel <c>-1</c>. The interceptor MUST NOT stamp <c>-1</c> onto
    /// entities — <c>-1</c> is a session-context value, never a row-data
    /// value. In system mode the interceptor leaves <see cref="ITenantOwned.TenantId"/>
    /// alone (<c>0</c> stays <c>0</c>; an explicitly-set non-zero value stays
    /// as written) so the caller is forced to choose a real tenant on each
    /// row.
    /// </para>
    /// <para>
    /// The audit log's <c>DomainEvent</c> / <c>DomainEventRow</c> carries a
    /// nullable <c>TenantId</c> (Sprint 4 G1 #4) and intentionally does NOT
    /// implement <see cref="ITenantOwned"/> — it bypasses this interceptor.
    /// Suite-wide system events flow through <c>DbEventPublisher</c>, which
    /// writes a <c>NULL</c> <c>TenantId</c>; the new RLS opt-in clause on
    /// <c>audit.events</c> (Sprint 5 G1-3) admits that write under the
    /// system-context sentinel.
    /// </para>
    /// </remarks>
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

        if (_tenant.IsSystem)
        {
            // Sprint 5 (G1-3) — in system mode the sentinel TenantId == -1
            // is a session-context value, NOT a valid row-data value.
            // Leave each entity's TenantId untouched so the caller must
            // explicitly choose a real owning tenant per row.
            return;
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
