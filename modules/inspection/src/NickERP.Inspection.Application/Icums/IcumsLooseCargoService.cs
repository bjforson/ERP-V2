using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Icums;

/// <summary>
/// Sprint 22 / B2.3 — "loose cargo" page service. Surfaces
/// <see cref="Scan"/>s whose <see cref="InspectionCase"/> has zero
/// <see cref="AuthorityDocument"/>s after a configurable grace window
/// (e.g., 4 hours post-capture). These are scans where the inbound
/// authority document never landed — analyst attention required.
///
/// <para>
/// **v1 parity.** v1's "loose cargo" page is the operator's daily
/// triage list: every container scanned but with no manifest. The v2
/// shape preserves the workflow but pivots the entity model — instead
/// of a separate "loose cargo" table, we project from the existing
/// scan + case + authority-document graph. Loose-ness is computed at
/// query time, not stored.
/// </para>
/// </summary>
public sealed class IcumsLooseCargoService
{
    /// <summary>Default grace period before a scan is considered "loose" (v1 parity).</summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromHours(4);

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<IcumsLooseCargoService> _logger;
    private readonly TimeProvider _clock;

    public IcumsLooseCargoService(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<IcumsLooseCargoService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// List "loose" cases — those whose
    /// <see cref="InspectionCase.OpenedAt"/> is older than the grace
    /// period and which have no
    /// <see cref="InspectionCase.Documents"/> attached. Ordered oldest
    /// first (highest urgency).
    /// </summary>
    public async Task<IReadOnlyList<LooseCargoRow>> ListAsync(
        TimeSpan? gracePeriod = null,
        int take = 200,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var grace = gracePeriod ?? DefaultGracePeriod;
        var cutoff = _clock.GetUtcNow() - grace;
        if (take < 1) take = 1;
        if (take > 500) take = 500;

        // Cases older than the cutoff with zero authority documents.
        // Use a left-join via subquery so we don't pull the full
        // documents collection into memory.
        var docCaseIds = _db.AuthorityDocuments.AsNoTracking()
            .Where(d => d.CaseId != Guid.Empty)
            .Select(d => d.CaseId)
            .Distinct();

        var rows = await _db.Cases.AsNoTracking()
            .Where(c => c.OpenedAt < cutoff)
            .Where(c => !docCaseIds.Contains(c.Id))
            .OrderBy(c => c.OpenedAt)
            .Take(take)
            .Select(c => new
            {
                c.Id,
                c.SubjectIdentifier,
                c.OpenedAt,
                c.State,
                c.LocationId,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0) return Array.Empty<LooseCargoRow>();

        var locIds = rows.Select(r => r.LocationId).Distinct().ToArray();
        var locNames = await _db.Locations.AsNoTracking()
            .Where(l => locIds.Contains(l.Id))
            .Select(l => new { l.Id, l.Name })
            .ToDictionaryAsync(l => l.Id, l => l.Name, ct)
            .ConfigureAwait(false);

        var now = _clock.GetUtcNow();
        return rows.Select(r =>
        {
            locNames.TryGetValue(r.LocationId, out var locName);
            return new LooseCargoRow(
                r.Id,
                r.SubjectIdentifier,
                r.OpenedAt,
                (now - r.OpenedAt),
                r.State.ToString(),
                r.LocationId,
                locName);
        }).ToList();
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; IcumsLooseCargoService must run inside a tenant-aware request scope.");
    }
}

/// <summary>One row on the loose-cargo page.</summary>
public sealed record LooseCargoRow(
    Guid CaseId,
    string SubjectIdentifier,
    DateTimeOffset OpenedAt,
    TimeSpan AgeWaiting,
    string State,
    Guid LocationId,
    string? LocationName);
