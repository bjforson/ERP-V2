using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// VP6 (locked 2026-05-02) Phase B — admin service for managing
/// <see cref="AnalysisService"/>s within a tenant: create, rename,
/// add/remove locations, add/remove users, delete (with un-deletable
/// guard for the built-in "All Locations" service).
///
/// <para>
/// **Tenant context.** Every method assumes <c>app.tenant_id</c> is set
/// by <c>TenantConnectionInterceptor</c> for the calling
/// <see cref="ITenantContext"/> — RLS narrows reads + writes
/// automatically. Callers should be inside a tenant-aware request scope
/// (Razor admin page, API endpoint, test fixture with a resolved tenant).
/// </para>
///
/// <para>
/// **Service-layer guards.** The "All Locations" built-in service:
/// (1) cannot be deleted (a database trigger also enforces this — defence
/// in depth);
/// (2) cannot have its <c>Name</c> changed (the trigger does NOT enforce
/// this — admin must respect it at the service layer or via the UI form
/// disabling the field);
/// (3) cannot have a location removed from it (the All Locations service
/// must always cover every location for unrouted-cases-impossible).
/// </para>
///
/// <para>
/// **Multi-membership.** When
/// <see cref="NickERP.Platform.Tenancy.Entities.Tenant.AllowMultiServiceMembership"/>
/// is <c>false</c>, <see cref="AddUserAsync"/> rejects a second
/// membership for a user already assigned to another service in the same
/// tenant. Always allowed when the target is the "All Locations" service
/// because that grant is universal — the per-tenant config flag has no
/// reason to block it.
/// </para>
/// </summary>
public sealed class AnalysisServiceAdminService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AnalysisServiceAdminService> _logger;

    public AnalysisServiceAdminService(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<AnalysisServiceAdminService> logger)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Create a new (non-built-in) <see cref="AnalysisService"/> for the
    /// current tenant. Returns the new service id. Throws
    /// <see cref="InvalidOperationException"/> on a duplicate name within
    /// the tenant — the unique index <c>ux_analysis_services_tenant_name</c>
    /// enforces this in the DB.
    /// </summary>
    public async Task<Guid> CreateAsync(
        string name,
        string? description,
        Guid createdByUserId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureTenantResolved();

        var trimmed = name.Trim();
        if (trimmed.Length > 200)
            throw new ArgumentException("Name must be 200 characters or fewer.", nameof(name));

        // Block creating another row with Name = "All Locations" — it's
        // the bootstrap-only display name and a duplicate would confuse
        // analysts.
        if (string.Equals(trimmed, "All Locations", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The name 'All Locations' is reserved for the built-in service.");

        var row = new AnalysisService
        {
            Id = Guid.NewGuid(),
            TenantId = _tenant.TenantId,
            Name = trimmed,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IsBuiltInAllLocations = false,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _db.AnalysisServices.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new InvalidOperationException(
                $"An AnalysisService named '{trimmed}' already exists in this tenant.",
                ex);
        }

        _logger.LogInformation(
            "Created AnalysisService {ServiceId} '{Name}' in tenant {TenantId} by user {UserId}.",
            row.Id, row.Name, _tenant.TenantId, createdByUserId);

        return row.Id;
    }

    /// <summary>
    /// Rename a service. Rejected for the built-in "All Locations"
    /// service. Throws if the new name collides with another service in
    /// the tenant.
    /// </summary>
    public async Task RenameAsync(Guid serviceId, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        EnsureTenantResolved();

        var row = await LoadOwnedAsync(serviceId, ct).ConfigureAwait(false);
        if (row.IsBuiltInAllLocations)
            throw new InvalidOperationException(
                "The built-in 'All Locations' service cannot be renamed.");

        var trimmed = newName.Trim();
        if (trimmed.Length > 200)
            throw new ArgumentException("Name must be 200 characters or fewer.", nameof(newName));

        if (string.Equals(trimmed, "All Locations", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The name 'All Locations' is reserved for the built-in service.");

        row.Name = trimmed;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new InvalidOperationException(
                $"An AnalysisService named '{trimmed}' already exists in this tenant.",
                ex);
        }
    }

    /// <summary>
    /// Update the free-text description on a service. Allowed for all
    /// services including the built-in one (admin can document its
    /// purpose). Pass <c>null</c> or empty to clear.
    /// </summary>
    public async Task UpdateDescriptionAsync(Guid serviceId, string? description, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var row = await LoadOwnedAsync(serviceId, ct).ConfigureAwait(false);
        row.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Add a location to a service's scope. Idempotent — re-adding the
    /// same location is a no-op (we check first; the composite PK on
    /// <c>(AnalysisServiceId, LocationId)</c> would also reject).
    /// </summary>
    public async Task AddLocationAsync(Guid serviceId, Guid locationId, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        // Confirm both rows exist and belong to the tenant (RLS narrows).
        _ = await LoadOwnedAsync(serviceId, ct).ConfigureAwait(false);
        var locExists = await _db.Locations.AsNoTracking()
            .AnyAsync(l => l.Id == locationId, ct)
            .ConfigureAwait(false);
        if (!locExists)
            throw new InvalidOperationException($"Location {locationId} not found in this tenant.");

        var alreadyJoined = await _db.AnalysisServiceLocations.AsNoTracking()
            .AnyAsync(asl => asl.AnalysisServiceId == serviceId && asl.LocationId == locationId, ct)
            .ConfigureAwait(false);
        if (alreadyJoined) return;

        _db.AnalysisServiceLocations.Add(new AnalysisServiceLocation
        {
            AnalysisServiceId = serviceId,
            LocationId = locationId,
            TenantId = _tenant.TenantId,
            AddedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove a location from a service's scope. Rejected for the
    /// built-in "All Locations" service — the unrouted-cases-impossible
    /// invariant requires it always cover every location.
    /// </summary>
    public async Task RemoveLocationAsync(Guid serviceId, Guid locationId, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var row = await LoadOwnedAsync(serviceId, ct).ConfigureAwait(false);
        if (row.IsBuiltInAllLocations)
            throw new InvalidOperationException(
                "Locations cannot be removed from the built-in 'All Locations' service "
                + "(unrouted-cases-impossible invariant).");

        var join = await _db.AnalysisServiceLocations
            .FirstOrDefaultAsync(asl => asl.AnalysisServiceId == serviceId && asl.LocationId == locationId, ct)
            .ConfigureAwait(false);
        if (join is null) return;

        _db.AnalysisServiceLocations.Remove(join);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Add a user to a service's membership. Honours the tenant's
    /// <see cref="NickERP.Platform.Tenancy.Entities.Tenant.AllowMultiServiceMembership"/>
    /// flag — when <c>false</c>, rejects the add if the user is already
    /// a member of any OTHER service in this tenant. The "All Locations"
    /// service is exempt from the multi-membership check (it's universal
    /// access; blocking it would undermine the bootstrap default).
    /// </summary>
    public async Task AddUserAsync(
        Guid serviceId,
        Guid userId,
        Guid? assignedByUserId,
        bool allowMultiServiceMembership,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var row = await LoadOwnedAsync(serviceId, ct).ConfigureAwait(false);

        var alreadyMember = await _db.AnalysisServiceUsers.AsNoTracking()
            .AnyAsync(u => u.AnalysisServiceId == serviceId && u.UserId == userId, ct)
            .ConfigureAwait(false);
        if (alreadyMember) return;

        if (!allowMultiServiceMembership && !row.IsBuiltInAllLocations)
        {
            var otherMembership = await _db.AnalysisServiceUsers.AsNoTracking()
                .AnyAsync(u => u.UserId == userId && u.AnalysisServiceId != serviceId, ct)
                .ConfigureAwait(false);
            if (otherMembership)
                throw new InvalidOperationException(
                    $"User {userId} is already a member of another service; "
                    + "this tenant does not allow multi-service membership. "
                    + "Remove the existing membership first or enable AllowMultiServiceMembership.");
        }

        _db.AnalysisServiceUsers.Add(new AnalysisServiceUser
        {
            AnalysisServiceId = serviceId,
            UserId = userId,
            TenantId = _tenant.TenantId,
            AssignedAt = DateTimeOffset.UtcNow,
            AssignedByUserId = assignedByUserId,
        });
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove a user's membership from a service. Allowed on the "All
    /// Locations" service (admin can revoke universal access for an
    /// individual user without deleting the service).
    /// </summary>
    public async Task RemoveUserAsync(Guid serviceId, Guid userId, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        _ = await LoadOwnedAsync(serviceId, ct).ConfigureAwait(false);

        var join = await _db.AnalysisServiceUsers
            .FirstOrDefaultAsync(u => u.AnalysisServiceId == serviceId && u.UserId == userId, ct)
            .ConfigureAwait(false);
        if (join is null) return;

        _db.AnalysisServiceUsers.Remove(join);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a non-built-in <see cref="AnalysisService"/>. Rejected for
    /// the built-in "All Locations" service at the service layer; the
    /// database also has a BEFORE DELETE trigger that raises an
    /// exception (defence in depth). Cascades to the junction tables
    /// (FK <c>ON DELETE CASCADE</c>).
    /// </summary>
    public async Task DeleteAsync(Guid serviceId, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var row = await LoadOwnedAsync(serviceId, ct).ConfigureAwait(false);
        if (row.IsBuiltInAllLocations)
            throw new InvalidOperationException(
                "The built-in 'All Locations' service cannot be deleted.");

        _db.AnalysisServices.Remove(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; AnalysisServiceAdminService must run inside a tenant-aware request scope.");
    }

    private async Task<AnalysisService> LoadOwnedAsync(Guid serviceId, CancellationToken ct)
    {
        // RLS narrows by tenant; AsTracking so callers can mutate.
        var row = await _db.AnalysisServices
            .FirstOrDefaultAsync(s => s.Id == serviceId, ct)
            .ConfigureAwait(false);
        if (row is null)
            throw new InvalidOperationException($"AnalysisService {serviceId} not found in this tenant.");
        return row;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
    }
}
