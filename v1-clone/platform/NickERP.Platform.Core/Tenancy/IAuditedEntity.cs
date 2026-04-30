namespace NickERP.Platform.Core.Tenancy;

/// <summary>
/// Marker interface for entities whose insert/update/delete events should be
/// captured by the central audit log (Phase 4 onward). The platform's
/// <c>AuditingDbContextInterceptor</c> automatically emits an audit event
/// for any entity implementing this interface.
/// </summary>
public interface IAuditedEntity
{
    /// <summary>Username of the user who created this row.</summary>
    string? CreatedBy { get; set; }

    /// <summary>UTC timestamp when this row was created.</summary>
    DateTime CreatedAt { get; set; }

    /// <summary>Username of the user who last updated this row.</summary>
    string? UpdatedBy { get; set; }

    /// <summary>UTC timestamp of the last update (null if never updated).</summary>
    DateTime? UpdatedAt { get; set; }
}
