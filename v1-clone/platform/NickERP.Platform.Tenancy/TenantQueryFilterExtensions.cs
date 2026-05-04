using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NickERP.Platform.Core.Tenancy;
using System.Linq.Expressions;
using System.Reflection;

namespace NickERP.Platform.Tenancy;

/// <summary>
/// Extensions for applying tenant query filters to a <see cref="DbContext"/>.
///
/// Modules call <c>builder.ApplyTenantFilters(_tenantContext)</c> at the end of
/// <c>OnModelCreating</c>. The helper walks every entity type in the model
/// and registers a global query filter on any that implement <see cref="ITenantOwned"/>:
///
/// <code>
///   modelBuilder.Entity&lt;T&gt;().HasQueryFilter(e => e.TenantId == _tenantContext.TenantId);
/// </code>
///
/// Important: the tenant context is captured by reference at model build time,
/// so EF Core re-evaluates it on every query (it does NOT bake in the value
/// from when OnModelCreating ran). This is the standard EF Core pattern for
/// runtime-resolved query filters.
/// </summary>
public static class TenantQueryFilterExtensions
{
    /// <summary>
    /// Walks every entity type in the model and applies a tenant query filter
    /// to any that implement <see cref="ITenantOwned"/>. Call from
    /// <c>OnModelCreating</c> at the END (after entity configurations).
    /// </summary>
    public static void ApplyTenantFilters(this ModelBuilder modelBuilder, ITenantContext tenantContext)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                var method = typeof(TenantQueryFilterExtensions)
                    .GetMethod(nameof(ApplyFilter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(entityType.ClrType);
                method.Invoke(null, new object[] { modelBuilder, tenantContext });
            }
        }
    }

    private static void ApplyFilter<T>(ModelBuilder modelBuilder, ITenantContext tenantContext)
        where T : class, ITenantOwned
    {
        modelBuilder.Entity<T>().HasQueryFilter(e => e.TenantId == tenantContext.TenantId);
    }

    /// <summary>
    /// Per-entity helper for cases where modules want to register the filter manually
    /// (e.g. inside an IEntityTypeConfiguration).
    /// </summary>
    public static EntityTypeBuilder<T> AddTenantQueryFilter<T>(
        this EntityTypeBuilder<T> builder,
        ITenantContext tenantContext)
        where T : class, ITenantOwned
    {
        builder.HasQueryFilter(e => e.TenantId == tenantContext.TenantId);
        return builder;
    }
}
