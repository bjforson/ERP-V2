using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Icums;

/// <summary>
/// Sprint 22 / B2.3 — BOE lookup admin service. Search by declaration /
/// reference number, container number, or date range across
/// <see cref="AuthorityDocument"/> rows whose
/// <see cref="AuthorityDocument.DocumentType"/> matches the BOE
/// document-type set (configurable; defaults to <c>BOE</c>).
///
/// <para>
/// **Vendor-shaped naming, vendor-neutral entity.** BOE = Bill of Entry
/// in customs parlance; the admin page is BOE-flavoured for operator
/// recognition. The entity is just an <see cref="AuthorityDocument"/>
/// with a configurable type tag — nothing BOE-specific in the schema.
/// </para>
/// </summary>
public sealed class IcumsBoeLookupService
{
    /// <summary>Default <see cref="AuthorityDocument.DocumentType"/> values treated as BOE-flavoured. Tenants can extend via <see cref="SearchAsync"/>.</summary>
    public static readonly IReadOnlyList<string> DefaultBoeTypes = new[] { "BOE" };

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<IcumsBoeLookupService> _logger;

    public IcumsBoeLookupService(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<IcumsBoeLookupService> logger)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
    }

    /// <summary>
    /// Search BOE documents. Empty <paramref name="query"/> + no date
    /// range returns the most recent <paramref name="take"/> rows.
    /// </summary>
    public async Task<IReadOnlyList<BoeLookupRow>> SearchAsync(
        string? query,
        DateTimeOffset? receivedFromUtc = null,
        DateTimeOffset? receivedToUtc = null,
        IReadOnlyList<string>? documentTypes = null,
        int take = 100,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        if (take < 1) take = 1;
        if (take > 500) take = 500;
        var types = documentTypes is { Count: > 0 } ? documentTypes : DefaultBoeTypes;

        IQueryable<AuthorityDocument> q = _db.AuthorityDocuments.AsNoTracking()
            .Where(d => types.Contains(d.DocumentType));

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            q = q.Where(d =>
                d.ReferenceNumber.Contains(needle) ||
                (d.Case != null && d.Case.SubjectIdentifier.Contains(needle)));
        }
        if (receivedFromUtc is DateTimeOffset from) q = q.Where(d => d.ReceivedAt >= from);
        if (receivedToUtc is DateTimeOffset to) q = q.Where(d => d.ReceivedAt < to);

        var scalars = await q
            .OrderByDescending(d => d.ReceivedAt)
            .Take(take)
            .Select(d => new
            {
                d.Id,
                d.CaseId,
                d.ExternalSystemInstanceId,
                d.DocumentType,
                d.ReferenceNumber,
                d.ReceivedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (scalars.Count == 0) return Array.Empty<BoeLookupRow>();

        // Two-step join (see IcumsDownloadQueueAdminService.ListAsync) —
        // EF in-memory drops rows when an FK has no match in the
        // navigation traversal, but Postgres handles a LEFT JOIN fine.
        // Keeping the same pattern keeps tests parity-clean.
        var caseIds = scalars
            .Where(s => s.CaseId != Guid.Empty)
            .Select(s => s.CaseId).Distinct().ToArray();
        var esiIds = scalars
            .Select(s => s.ExternalSystemInstanceId).Distinct().ToArray();

        var caseLookup = await _db.Cases.AsNoTracking()
            .Where(c => caseIds.Contains(c.Id))
            .Select(c => new { c.Id, c.SubjectIdentifier })
            .ToDictionaryAsync(c => c.Id, c => c.SubjectIdentifier, ct)
            .ConfigureAwait(false);
        var esiLookup = await _db.ExternalSystemInstances.AsNoTracking()
            .Where(e => esiIds.Contains(e.Id))
            .Select(e => new { e.Id, e.DisplayName })
            .ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct)
            .ConfigureAwait(false);

        return scalars.Select(s =>
        {
            string? caseSubject = null;
            if (s.CaseId != Guid.Empty && caseLookup.TryGetValue(s.CaseId, out var subject))
            {
                caseSubject = subject;
            }
            esiLookup.TryGetValue(s.ExternalSystemInstanceId, out var esiName);
            return new BoeLookupRow(
                s.Id,
                s.CaseId,
                s.ExternalSystemInstanceId,
                esiName,
                s.DocumentType,
                s.ReferenceNumber,
                caseSubject,
                s.ReceivedAt);
        }).ToList();
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; IcumsBoeLookupService must run inside a tenant-aware request scope.");
    }
}

/// <summary>One row in the BOE lookup result.</summary>
public sealed record BoeLookupRow(
    Guid DocumentId,
    Guid CaseId,
    Guid ExternalSystemInstanceId,
    string? ExternalSystemDisplayName,
    string DocumentType,
    string ReferenceNumber,
    string? CaseSubjectIdentifier,
    DateTimeOffset ReceivedAt);
