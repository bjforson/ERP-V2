using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.AnalysisServices;

/// <summary>
/// Default <see cref="IAnalysisServiceBootstrap"/> implementation.
/// Writes against <see cref="InspectionDbContext"/> directly. Callers
/// must already be in a tenant-aware request scope: the
/// <c>TenantConnectionInterceptor</c> has set <c>app.tenant_id</c>
/// before this method is invoked, so the <c>tenant_isolation_*</c> RLS
/// policy admits the INSERT into <c>analysis_services</c>.
///
/// <para>
/// Idempotency is enforced by the unique partial index
/// <c>ux_analysis_services_tenant_built_in</c> in the database.
/// Concurrent calls race on the unique violation; the loser swallows
/// the error and returns <c>false</c>.
/// </para>
/// </summary>
public sealed class AnalysisServiceBootstrap : IAnalysisServiceBootstrap
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<AnalysisServiceBootstrap> _logger;

    public AnalysisServiceBootstrap(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<AnalysisServiceBootstrap> logger)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> EnsureAllLocationsServiceAsync(
        long tenantId,
        Guid? createdByUserId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId <= 0)
            throw new ArgumentOutOfRangeException(nameof(tenantId), "TenantId must be positive.");

        // The caller's tenant context must already match. We don't
        // use SetSystemContext here — bootstrap is always invoked in
        // the tenant's own request scope (e.g., post-tenant-create in
        // Tenants.razor uses the new tenant's id, which the calling
        // code is responsible for setting on ITenantContext first).
        if (_tenant.TenantId != tenantId)
        {
            _tenant.SetTenant(tenantId);
        }

        // Idempotency check first — avoid the INSERT round-trip when
        // possible. The unique partial index is the authoritative
        // guard; this is the optimistic fast-path.
        var existing = await _db.AnalysisServices
            .Where(s => s.TenantId == tenantId && s.IsBuiltInAllLocations)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing != Guid.Empty)
        {
            _logger.LogDebug(
                "All Locations service already exists for tenant {TenantId} (service {ServiceId}); skipping bootstrap.",
                tenantId, existing);
            return false;
        }

        var row = new AnalysisService
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "All Locations",
            Description = "Built-in service that includes every location in the tenant. Cannot be deleted; admins manage analyst access via membership.",
            IsBuiltInAllLocations = true,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.AnalysisServices.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Bootstrapped All Locations service {ServiceId} for tenant {TenantId} (createdBy={CreatedByUserId}).",
                row.Id, tenantId, createdByUserId);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost the race against a concurrent caller. The other
            // caller's row is now the one. Idempotent outcome.
            _logger.LogDebug(
                ex,
                "Concurrent bootstrap won for tenant {TenantId}; swallowing unique-violation.",
                tenantId);
            // Detach our optimistic entity so the next SaveChanges doesn't retry it.
            _db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // PG SQLSTATE 23505 = unique_violation. Bubbled up via
        // Npgsql.PostgresException → DbUpdateException.InnerException.
        return ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505";
    }
}
