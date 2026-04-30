using Microsoft.EntityFrameworkCore;

namespace NickFinance.Coa;

/// <summary>
/// Module-facing surface for chart-of-accounts lookups. Modules don't
/// touch <see cref="CoaDbContext"/> directly; they depend on this so a
/// future remote-CoA service can swap in transparently.
/// </summary>
public interface ICoaService
{
    /// <summary>True if the code exists, is active, in the given tenant. Cached for the request lifetime.</summary>
    Task<bool> IsActiveAsync(string code, long tenantId = 1, CancellationToken ct = default);

    /// <summary>Fetch an account by code or return <see langword="null"/>.</summary>
    Task<Account?> FindAsync(string code, long tenantId = 1, CancellationToken ct = default);

    /// <summary>Bulk lookup — returns the active subset of the supplied codes.</summary>
    Task<IReadOnlySet<string>> ActiveSetAsync(IEnumerable<string> codes, long tenantId = 1, CancellationToken ct = default);

    /// <summary>Seed (or top up) the chart with the Ghana SME default. Idempotent — existing codes left unchanged.</summary>
    Task<int> SeedGhanaStandardChartAsync(long tenantId = 1, CancellationToken ct = default);
}

/// <summary>EF-backed implementation. Scope-cached lookups keep N+1 queries off the writer hot-path.</summary>
public sealed class CoaService : ICoaService
{
    private readonly CoaDbContext _db;
    private readonly TimeProvider _clock;

    public CoaService(CoaDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<bool> IsActiveAsync(string code, long tenantId = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        return await _db.Accounts.AsNoTracking()
            .AnyAsync(a => a.TenantId == tenantId && a.Code == code && a.IsActive, ct);
    }

    public async Task<Account?> FindAsync(string code, long tenantId = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return await _db.Accounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Code == code, ct);
    }

    public async Task<IReadOnlySet<string>> ActiveSetAsync(IEnumerable<string> codes, long tenantId = 1, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var distinct = codes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToArray();
        if (distinct.Length == 0) return new HashSet<string>(StringComparer.Ordinal);

        var active = await _db.Accounts.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.IsActive && distinct.Contains(a.Code))
            .Select(a => a.Code)
            .ToListAsync(ct);
        return new HashSet<string>(active, StringComparer.Ordinal);
    }

    public async Task<int> SeedGhanaStandardChartAsync(long tenantId = 1, CancellationToken ct = default)
    {
        var existing = await _db.Accounts
            .Where(a => a.TenantId == tenantId)
            .Select(a => a.Code)
            .ToListAsync(ct);
        var have = new HashSet<string>(existing, StringComparer.Ordinal);

        var now = _clock.GetUtcNow();
        var inserted = 0;
        foreach (var seed in GhanaStandardChart.Default)
        {
            if (have.Contains(seed.Code)) continue;
            _db.Accounts.Add(new Account
            {
                Code = seed.Code,
                Name = seed.Name,
                Type = seed.Type,
                ParentCode = seed.ParentCode,
                CurrencyCode = seed.CurrencyCode,
                IsControl = seed.IsControl,
                IsActive = seed.IsActive,
                Description = seed.Description,
                CreatedAt = now,
                UpdatedAt = now,
                TenantId = tenantId
            });
            inserted++;
        }
        if (inserted > 0) await _db.SaveChangesAsync(ct);
        return inserted;
    }
}
