using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NickERP.Inspection.Web.HealthChecks;

/// <summary>
/// Phase F5 — readiness probe for a Postgres-backed
/// <see cref="DbContext"/>. Runs <c>SELECT 1</c> via
/// <see cref="DatabaseFacade.ExecuteSqlRawAsync"/>; returns
/// <see cref="HealthStatus.Unhealthy"/> on any failure with the underlying
/// exception captured in <see cref="HealthCheckResult.Exception"/>.
///
/// Tiny custom probe rather than <c>AspNetCore.HealthChecks.NpgSql</c> —
/// avoids adding a third-party package whose .NET 10 compatibility we'd
/// have to verify, and gives us a consistent shape across all DB checks
/// (one concrete <see cref="DbContext"/> per probe, mirroring the host's
/// DI setup exactly).
/// </summary>
public sealed class PostgresHealthCheck<TDbContext> : IHealthCheck
    where TDbContext : DbContext
{
    private readonly TDbContext _db;
    private readonly string _name;

    public PostgresHealthCheck(TDbContext db, string name)
    {
        _db = db;
        _name = name;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ExecuteSqlRawAsync returns "rows affected"; we don't care
            // about the value — only that the query reaches the DB.
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy($"{_name} reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                description: $"{_name} unreachable: {ex.GetType().Name}",
                exception: ex);
        }
    }
}
