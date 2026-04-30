namespace NickERP.Platform.Core.Tenancy;

/// <summary>
/// Marker interface for entities that belong to a specific tenant.
/// Every business-data entity in every module SHOULD implement this from Phase 1 onward.
///
/// The platform's <c>TenantOwnedEntityInterceptor</c> automatically writes the
/// current tenant id on insert, and EF Core global query filters automatically
/// scope reads. Modules don't have to do anything except implement the interface
/// and call <c>builder.AddTenantQueryFilter&lt;T&gt;()</c> in <c>OnModelCreating</c>.
/// </summary>
public interface ITenantOwned
{
    long TenantId { get; set; }
}
