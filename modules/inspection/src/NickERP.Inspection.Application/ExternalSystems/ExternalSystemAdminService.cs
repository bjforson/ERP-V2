using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.ExternalSystems;

/// <summary>
/// Sprint 16 / LA1 extension (locked 2026-05-02) — admin service for
/// <see cref="ExternalSystemInstance"/>s within a tenant: register an
/// instance, set its <see cref="ExternalSystemBindingScope"/> + binding
/// rows in one atomic operation, list, deactivate.
///
/// <para>
/// **Three scopes.**
/// <list type="bullet">
///   <item><description><see cref="ExternalSystemBindingScope.PerLocation"/> — one binding row, one location.</description></item>
///   <item><description><see cref="ExternalSystemBindingScope.SubsetOfLocations"/> — two-or-more binding rows.</description></item>
///   <item><description><see cref="ExternalSystemBindingScope.Shared"/> — zero binding rows; covers every location implicitly.</description></item>
/// </list>
/// Per-scope cardinality is enforced by
/// <see cref="ExternalSystemBindingScopeValidation"/>; the service refuses
/// to persist invalid combinations.
/// </para>
///
/// <para>
/// **Tenant context.** Every method assumes <c>app.tenant_id</c> is set
/// by the platform's <c>TenantConnectionInterceptor</c> for the calling
/// <see cref="ITenantContext"/> — RLS narrows reads + writes
/// automatically. Callers should be inside a tenant-aware request scope
/// (admin Razor page, API endpoint, test fixture with a resolved tenant).
/// </para>
/// </summary>
public sealed class ExternalSystemAdminService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<ExternalSystemAdminService> _logger;
    private readonly TimeProvider _clock;

    public ExternalSystemAdminService(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<ExternalSystemAdminService> logger,
        TimeProvider? clock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Register a new <see cref="ExternalSystemInstance"/> with the given
    /// <paramref name="scope"/> + binding location ids in a single
    /// atomic SaveChanges. Validates that the binding count matches the
    /// scope and that every <paramref name="locationIds"/> is a real
    /// location in the tenant before writing.
    /// </summary>
    /// <returns>The id of the newly-created instance.</returns>
    /// <exception cref="ArgumentException">Validation failure (empty type code, scope mismatch, unknown location id).</exception>
    /// <exception cref="InvalidOperationException">Tenant context is not resolved.</exception>
    public async Task<Guid> RegisterAsync(
        string typeCode,
        string displayName,
        string? description,
        ExternalSystemBindingScope scope,
        IReadOnlyList<Guid> locationIds,
        string configJson = "{}",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(locationIds);

        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved. ExternalSystemAdminService requires a resolved tenant.");
        }

        var bindingError = ExternalSystemBindingScopeValidation.Validate(scope, locationIds.Count);
        if (bindingError is not null)
        {
            throw new ArgumentException(bindingError, nameof(scope));
        }

        // Distinct check — duplicate location ids in the same instance
        // collapse to one binding under the unique index but better to
        // reject up front.
        var distinctIds = locationIds.Distinct().ToList();
        if (distinctIds.Count != locationIds.Count)
        {
            throw new ArgumentException(
                "Duplicate location ids in bindings; each location may appear at most once per instance.",
                nameof(locationIds));
        }

        // Validate each location id resolves under the tenant. RLS narrows
        // the lookup, so an unknown id here is either a typo or a cross-
        // tenant reference; either way reject.
        if (distinctIds.Count > 0)
        {
            var foundCount = await _db.Locations
                .Where(l => distinctIds.Contains(l.Id))
                .CountAsync(ct);
            if (foundCount != distinctIds.Count)
            {
                throw new ArgumentException(
                    $"One or more location ids do not exist in the current tenant ({foundCount}/{distinctIds.Count} resolved).",
                    nameof(locationIds));
            }
        }

        var now = _clock.GetUtcNow();
        var instance = new ExternalSystemInstance
        {
            Id = Guid.NewGuid(),
            TypeCode = typeCode,
            DisplayName = displayName.Trim(),
            Description = description?.Trim(),
            Scope = scope,
            ConfigJson = configJson,
            IsActive = true,
            CreatedAt = now,
            TenantId = _tenant.TenantId
        };
        _db.ExternalSystemInstances.Add(instance);

        foreach (var locationId in distinctIds)
        {
            _db.ExternalSystemBindings.Add(new ExternalSystemBinding
            {
                Id = Guid.NewGuid(),
                ExternalSystemInstanceId = instance.Id,
                LocationId = locationId,
                Role = "primary",
                CreatedAt = now,
                TenantId = _tenant.TenantId
            });
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Registered ExternalSystemInstance {Id} type={TypeCode} scope={Scope} bindings={Count} tenant={TenantId}",
            instance.Id, typeCode, scope, distinctIds.Count, _tenant.TenantId);

        return instance.Id;
    }

    /// <summary>
    /// Look up the location ids that <paramref name="instanceId"/> serves.
    /// Returns the empty list for <see cref="ExternalSystemBindingScope.Shared"/>
    /// instances — callers should check <see cref="GetScopeAsync"/> if
    /// they need to distinguish "Shared (covers all)" from "PerLocation
    /// with no bindings yet (invalid)".
    /// </summary>
    public async Task<IReadOnlyList<Guid>> GetBindingLocationsAsync(
        Guid instanceId, CancellationToken ct = default)
    {
        return await _db.ExternalSystemBindings
            .AsNoTracking()
            .Where(b => b.ExternalSystemInstanceId == instanceId)
            .Select(b => b.LocationId)
            .ToListAsync(ct);
    }

    /// <summary>Look up the scope of an instance by id, or null if not in this tenant.</summary>
    public async Task<ExternalSystemBindingScope?> GetScopeAsync(
        Guid instanceId, CancellationToken ct = default)
    {
        return await _db.ExternalSystemInstances
            .AsNoTracking()
            .Where(e => e.Id == instanceId)
            .Select(e => (ExternalSystemBindingScope?)e.Scope)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Resolve the set of <see cref="ExternalSystemInstance"/> ids that
    /// serve the given <paramref name="locationId"/> in the current
    /// tenant. Encapsulates the three-scope lookup:
    /// <list type="bullet">
    ///   <item><description><see cref="ExternalSystemBindingScope.Shared"/> instances always match.</description></item>
    ///   <item><description><see cref="ExternalSystemBindingScope.PerLocation"/> + <see cref="ExternalSystemBindingScope.SubsetOfLocations"/> match when a binding exists for <paramref name="locationId"/>.</description></item>
    /// </list>
    /// Active filter applied — inactive instances are excluded.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ResolveServingInstancesAsync(
        Guid locationId, CancellationToken ct = default)
    {
        // Shared (no bindings) — match every instance regardless of bindings.
        var shared = _db.ExternalSystemInstances
            .Where(e => e.IsActive && e.Scope == ExternalSystemBindingScope.Shared)
            .Select(e => e.Id);

        // PerLocation + SubsetOfLocations — match when a binding exists.
        var bound = _db.ExternalSystemBindings
            .Where(b => b.LocationId == locationId
                        && b.Instance != null
                        && b.Instance.IsActive
                        && (b.Instance.Scope == ExternalSystemBindingScope.PerLocation
                            || b.Instance.Scope == ExternalSystemBindingScope.SubsetOfLocations))
            .Select(b => b.ExternalSystemInstanceId);

        return await shared.Union(bound).Distinct().ToListAsync(ct);
    }
}
