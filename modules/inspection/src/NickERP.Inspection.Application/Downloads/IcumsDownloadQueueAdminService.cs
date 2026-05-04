using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Downloads;

/// <summary>
/// Sprint 22 / B2.2 — admin service for the ICUMS download queue page
/// at <c>/admin/icums/download-queue</c>. v1's "download queue" was the
/// inbound stream of pulled BOE / CMR / IM rows; in v2 those land as
/// <see cref="AuthorityDocument"/> rows attached to a case (matched) or
/// floating with no case linkage (unmatched, awaiting reconciliation).
///
/// <para>
/// **Vendor-neutral entity, vendor-shaped page.** The queue surfaces
/// every <see cref="AuthorityDocument"/> regardless of source authority;
/// the page filters by <see cref="ExternalSystemInstance"/> (which the
/// operator picks from the ICUMS-flavoured set) so analysts see the
/// stream they recognise.
/// </para>
///
/// <para>
/// **Pull-cursor state.** The page also surfaces the
/// <see cref="OutcomePullCursor"/> rows so operators can see lag /
/// consecutive-failure counts without grepping logs. Read-only here —
/// cursor mutation lives in
/// <see cref="NickERP.Inspection.Web.Services.OutcomePullWorker"/>.
/// </para>
///
/// <para>
/// **Re-link.** When the auto-match in
/// <c>PostHocOutcomeWriter.TryFindCaseAsync</c> mis-routes (or fails),
/// admins can override via <see cref="RelinkAsync"/>. The override is
/// audit-logged but currently scoped to changing
/// <c>AuthorityDocument.CaseId</c> only — does NOT propagate any
/// downstream state. Power-user feature; the runbook documents the
/// downstream knock-ons.
/// </para>
/// </summary>
public sealed class IcumsDownloadQueueAdminService
{
    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly ILogger<IcumsDownloadQueueAdminService> _logger;
    private readonly TimeProvider _clock;

    public IcumsDownloadQueueAdminService(
        InspectionDbContext db,
        ITenantContext tenant,
        ILogger<IcumsDownloadQueueAdminService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _tenant = tenant;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Filtered, paged list of <see cref="AuthorityDocument"/> rows
    /// (the "download queue"). Returns a read-only DTO projection so
    /// the page does not bind tracked entities.
    /// </summary>
    public async Task<DownloadPage> ListAsync(
        DownloadQueueFilter? filters,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 200) pageSize = 200;
        filters ??= new DownloadQueueFilter();

        IQueryable<AuthorityDocument> q = _db.AuthorityDocuments.AsNoTracking();

        if (filters.DocumentTypes is { Count: > 0 } types)
        {
            q = q.Where(d => types.Contains(d.DocumentType));
        }
        if (filters.ExternalSystemInstanceId is Guid esiId)
        {
            q = q.Where(d => d.ExternalSystemInstanceId == esiId);
        }
        if (filters.ReceivedFromUtc is DateTimeOffset from)
        {
            q = q.Where(d => d.ReceivedAt >= from);
        }
        if (filters.ReceivedToUtc is DateTimeOffset to)
        {
            q = q.Where(d => d.ReceivedAt < to);
        }
        if (!string.IsNullOrWhiteSpace(filters.SearchText))
        {
            var needle = filters.SearchText.Trim();
            q = q.Where(d =>
                d.ReferenceNumber.Contains(needle) ||
                (d.Case != null && d.Case.SubjectIdentifier.Contains(needle)));
        }
        if (filters.MatchStatus is DownloadMatchStatus ms)
        {
            switch (ms)
            {
                case DownloadMatchStatus.Matched:
                    q = q.Where(d => d.CaseId != Guid.Empty);
                    break;
                case DownloadMatchStatus.Unmatched:
                    q = q.Where(d => d.CaseId == Guid.Empty);
                    break;
            }
        }

        var total = await q.CountAsync(ct).ConfigureAwait(false);

        // Two-step projection to avoid the EF in-memory provider's
        // INNER-JOIN behaviour on required navigations (when CaseId =
        // Guid.Empty there's no matching case row, and Selecting through
        // d.Case in the same query silently drops the row from the result
        // set under the in-memory provider). Step 1 fetches scalars +
        // joinable lookups separately; step 2 stitches them in memory.
        var scalars = await q
            .OrderByDescending(d => d.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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

        if (scalars.Count == 0)
        {
            return new DownloadPage(Array.Empty<DownloadRow>(), total, page, pageSize);
        }

        var caseIds = scalars
            .Where(s => s.CaseId != Guid.Empty)
            .Select(s => s.CaseId)
            .Distinct()
            .ToArray();
        var esiIds = scalars
            .Select(s => s.ExternalSystemInstanceId)
            .Distinct()
            .ToArray();

        var caseLookup = await _db.Cases.AsNoTracking()
            .Where(c => caseIds.Contains(c.Id))
            .Select(c => new { c.Id, c.SubjectIdentifier })
            .ToDictionaryAsync(c => c.Id, c => c.SubjectIdentifier, ct)
            .ConfigureAwait(false);
        var esiLookup = await _db.ExternalSystemInstances.AsNoTracking()
            .Where(e => esiIds.Contains(e.Id))
            .Select(e => new { e.Id, e.DisplayName, e.TypeCode })
            .ToDictionaryAsync(e => e.Id, e => (e.DisplayName, e.TypeCode), ct)
            .ConfigureAwait(false);

        var rows = scalars.Select(s =>
        {
            esiLookup.TryGetValue(s.ExternalSystemInstanceId, out var esi);
            string? caseSubject = null;
            if (s.CaseId != Guid.Empty && caseLookup.TryGetValue(s.CaseId, out var subject))
            {
                caseSubject = subject;
            }
            return new DownloadRow(
                s.Id,
                s.CaseId,
                s.ExternalSystemInstanceId,
                esi.DisplayName,
                esi.TypeCode,
                caseSubject,
                s.DocumentType,
                s.ReferenceNumber,
                s.ReceivedAt,
                s.CaseId != Guid.Empty);
        }).ToList();

        return new DownloadPage(rows, total, page, pageSize);
    }

    /// <summary>
    /// Fetch the raw payload for one document. Used by the queue page's
    /// "View payload" modal and by the BOE lookup detail panel.
    /// </summary>
    public async Task<DownloadPayload?> GetPayloadAsync(
        Guid documentId, CancellationToken ct = default)
    {
        EnsureTenantResolved();
        return await _db.AuthorityDocuments.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new DownloadPayload(
                d.Id,
                d.CaseId,
                d.ExternalSystemInstanceId,
                d.DocumentType,
                d.ReferenceNumber,
                d.PayloadJson,
                d.ReceivedAt))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Override the case link on a document — used when the auto-match
    /// failed or routed to the wrong case. Pass
    /// <see cref="Guid.Empty"/> to detach.
    /// </summary>
    public async Task<RelinkResult> RelinkAsync(
        Guid documentId, Guid newCaseId, Guid actorUserId,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();

        var doc = await _db.AuthorityDocuments
            .FirstOrDefaultAsync(d => d.Id == documentId, ct)
            .ConfigureAwait(false);
        if (doc is null) return new RelinkResult(false, "Document not found in this tenant.");

        if (newCaseId != Guid.Empty)
        {
            var caseExists = await _db.Cases.AsNoTracking()
                .AnyAsync(c => c.Id == newCaseId, ct)
                .ConfigureAwait(false);
            if (!caseExists)
            {
                return new RelinkResult(false, $"Target case {newCaseId} not found in this tenant.");
            }
        }

        var oldCaseId = doc.CaseId;
        if (oldCaseId == newCaseId)
        {
            return new RelinkResult(true, "Already linked to that case — no change.");
        }
        doc.CaseId = newCaseId;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Relinked AuthorityDocument {DocumentId} from case {OldCaseId} to {NewCaseId} "
            + "(tenant {TenantId}, by user {ActorUserId}).",
            documentId, oldCaseId, newCaseId, _tenant.TenantId, actorUserId);

        return new RelinkResult(true, $"Relinked from {oldCaseId} to {newCaseId}.");
    }

    /// <summary>
    /// Pull-cursor state for every <see cref="ExternalSystemInstance"/>
    /// in the tenant. Read-only; surfaces lag-seconds + consecutive
    /// failure counts so operators can see when the
    /// <c>OutcomePullWorker</c> is wedged without log-grepping.
    /// </summary>
    public async Task<IReadOnlyList<PullCursorRow>> GetPullCursorsAsync(
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        var now = _clock.GetUtcNow();
        var cursors = await _db.OutcomePullCursors.AsNoTracking()
            .Select(c => new
            {
                c.ExternalSystemInstanceId,
                c.LastSuccessfulPullAt,
                c.LastPullWindowUntil,
                c.ConsecutiveFailures,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (cursors.Count == 0) return Array.Empty<PullCursorRow>();

        // See ListAsync: separate lookup avoids the EF in-memory
        // INNER-JOIN-on-required-nav behaviour for unmatched FKs.
        var esiIds = cursors.Select(c => c.ExternalSystemInstanceId).Distinct().ToArray();
        var esiLookup = await _db.ExternalSystemInstances.AsNoTracking()
            .Where(e => esiIds.Contains(e.Id))
            .Select(e => new { e.Id, e.DisplayName, e.TypeCode })
            .ToDictionaryAsync(e => e.Id, e => (e.DisplayName, e.TypeCode), ct)
            .ConfigureAwait(false);

        return cursors.Select(c =>
        {
            esiLookup.TryGetValue(c.ExternalSystemInstanceId, out var esi);
            var lag = (now - c.LastSuccessfulPullAt).TotalSeconds;
            return new PullCursorRow(
                c.ExternalSystemInstanceId,
                esi.DisplayName,
                esi.TypeCode,
                c.LastSuccessfulPullAt,
                c.LastPullWindowUntil,
                c.ConsecutiveFailures,
                lag > 0 ? lag : 0);
        }).ToList();
    }

    /// <summary>
    /// Aggregate counts grouped by <see cref="AuthorityDocument.DocumentType"/>
    /// for the dashboard summary card.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetDocumentTypeCountsAsync(
        Guid? externalSystemInstanceId = null,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        IQueryable<AuthorityDocument> q = _db.AuthorityDocuments.AsNoTracking();
        if (externalSystemInstanceId is Guid esiId)
        {
            q = q.Where(d => d.ExternalSystemInstanceId == esiId);
        }
        var grouped = await q
            .GroupBy(d => d.DocumentType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return grouped.ToDictionary(x => x.Type, x => x.Count, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; IcumsDownloadQueueAdminService must run inside a tenant-aware request scope.");
    }
}

/// <summary>Filter for the download-queue page.</summary>
public sealed record DownloadQueueFilter
{
    /// <summary>Document types to include (BOE, CMR, IM, Manifest, etc.). Null/empty = all.</summary>
    public IReadOnlyList<string>? DocumentTypes { get; init; }

    /// <summary>Filter by source instance.</summary>
    public Guid? ExternalSystemInstanceId { get; init; }

    /// <summary>Inclusive lower bound on <c>ReceivedAt</c>.</summary>
    public DateTimeOffset? ReceivedFromUtc { get; init; }

    /// <summary>Exclusive upper bound on <c>ReceivedAt</c>.</summary>
    public DateTimeOffset? ReceivedToUtc { get; init; }

    /// <summary>Substring match across reference number + case subject.</summary>
    public string? SearchText { get; init; }

    /// <summary>Filter on whether a case is linked yet.</summary>
    public DownloadMatchStatus? MatchStatus { get; init; }
}

/// <summary>Has-case-link filter for the download queue.</summary>
public enum DownloadMatchStatus
{
    /// <summary><see cref="AuthorityDocument.CaseId"/> is non-empty (auto-match resolved).</summary>
    Matched = 1,
    /// <summary><see cref="AuthorityDocument.CaseId"/> is empty (auto-match failed or pending manual link).</summary>
    Unmatched = 2,
}

/// <summary>One row in the download-queue list.</summary>
public sealed record DownloadRow(
    Guid Id,
    Guid CaseId,
    Guid ExternalSystemInstanceId,
    string? ExternalSystemDisplayName,
    string? ExternalSystemTypeCode,
    string? CaseSubjectIdentifier,
    string DocumentType,
    string ReferenceNumber,
    DateTimeOffset ReceivedAt,
    bool IsMatched);

/// <summary>Paged list result.</summary>
public sealed record DownloadPage(
    IReadOnlyList<DownloadRow> Rows,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>Payload-fetch result.</summary>
public sealed record DownloadPayload(
    Guid Id,
    Guid CaseId,
    Guid ExternalSystemInstanceId,
    string DocumentType,
    string ReferenceNumber,
    string PayloadJson,
    DateTimeOffset ReceivedAt);

/// <summary>One pull-cursor row.</summary>
public sealed record PullCursorRow(
    Guid ExternalSystemInstanceId,
    string? ExternalSystemDisplayName,
    string? ExternalSystemTypeCode,
    DateTimeOffset LastSuccessfulPullAt,
    DateTimeOffset LastPullWindowUntil,
    int ConsecutiveFailures,
    double LagSeconds);

/// <summary>Outcome of a re-link call.</summary>
public sealed record RelinkResult(bool Success, string Notice);
