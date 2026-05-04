using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Application.Submissions;

/// <summary>
/// Sprint 22 / B2.1 — admin service for the ICUMS submission queue page
/// at <c>/admin/icums/submission-queue</c>. Centralises filtered listing,
/// per-row requeue, bulk requeue under a safety bound, and payload
/// fetch. Mirrors the
/// <see cref="NickERP.Inspection.Application.AnalysisServices.AnalysisServiceAdminService"/>
/// shape (constructor injection of <see cref="InspectionDbContext"/> +
/// <see cref="ITenantContext"/>; tenant resolution gate).
///
/// <para>
/// **Vendor-neutral entity, vendor-shaped page.** This service queries
/// the platform's <see cref="OutboundSubmission"/> entity and emits
/// vendor-neutral audit events. The "ICUMS" label is applied by the
/// caller (the Razor page) when filtering by external-system instance —
/// the route + UI text are operationally meaningful for analysts but the
/// underlying surface is generic.
/// </para>
///
/// <para>
/// **Bulk requeue is bounded.** <see cref="RequeueBulkAsync"/> caps the
/// number of rows it will mutate at <see cref="MaxBulkRequeueRows"/> per
/// call. The audit event records the count so a runaway operator click
/// shows up clearly in the audit trail.
/// </para>
///
/// <para>
/// **Tenant context.** Every method assumes <c>app.tenant_id</c> is set
/// by <c>TenantConnectionInterceptor</c> for the calling
/// <see cref="ITenantContext"/> — RLS narrows reads + writes
/// automatically. Callers must be inside a tenant-aware request scope.
/// </para>
/// </summary>
public sealed class IcumsSubmissionQueueAdminService
{
    /// <summary>
    /// Hard cap on rows mutated by a single
    /// <see cref="RequeueBulkAsync"/> call. Configurable via
    /// constructor override (test fixtures override to small numbers);
    /// defaults to 1000 — high enough that a "normal" operator action
    /// flips an entire failed-submission backlog, low enough that a
    /// runaway click cannot DOS the dispatcher.
    /// </summary>
    public const int MaxBulkRequeueRows = 1000;

    private readonly InspectionDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEventPublisher _events;
    private readonly ILogger<IcumsSubmissionQueueAdminService> _logger;
    private readonly TimeProvider _clock;

    public IcumsSubmissionQueueAdminService(
        InspectionDbContext db,
        ITenantContext tenant,
        IEventPublisher events,
        ILogger<IcumsSubmissionQueueAdminService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _tenant = tenant;
        _events = events;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Filtered, paged list of <see cref="OutboundSubmission"/> rows.
    /// Returns <see cref="SubmissionRow"/> projections (read-only DTOs)
    /// so the page does not bind to live tracking entities.
    /// </summary>
    /// <param name="filters">Optional filters; null fields ignored.</param>
    /// <param name="page">1-based page number (capped at 1).</param>
    /// <param name="pageSize">Page size (1 to 200; capped at 200).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SubmissionPage> ListAsync(
        SubmissionQueueFilter? filters,
        int page = 1,
        int pageSize = 50,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 1;
        if (pageSize > 200) pageSize = 200;
        filters ??= new SubmissionQueueFilter();

        IQueryable<OutboundSubmission> q = _db.OutboundSubmissions.AsNoTracking();

        if (filters.Statuses is { Count: > 0 } statuses)
        {
            q = q.Where(s => statuses.Contains(s.Status));
        }
        if (filters.ExternalSystemInstanceId is Guid esiId)
        {
            q = q.Where(s => s.ExternalSystemInstanceId == esiId);
        }
        if (filters.SubmittedFromUtc is DateTimeOffset from)
        {
            q = q.Where(s => s.SubmittedAt >= from);
        }
        if (filters.SubmittedToUtc is DateTimeOffset to)
        {
            q = q.Where(s => s.SubmittedAt < to);
        }
        if (!string.IsNullOrWhiteSpace(filters.SearchText))
        {
            var needle = filters.SearchText.Trim();
            q = q.Where(s =>
                s.IdempotencyKey.Contains(needle) ||
                (s.Case != null && s.Case.SubjectIdentifier.Contains(needle)));
        }

        var total = await q.CountAsync(ct).ConfigureAwait(false);

        // Highest priority first, then oldest in priority. Index
        // ix_outbound_tenant_status_priority_time covers this exactly.
        var rows = await q
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.SubmittedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SubmissionRow(
                s.Id,
                s.CaseId,
                s.ExternalSystemInstanceId,
                s.ExternalSystemInstance != null ? s.ExternalSystemInstance.DisplayName : null,
                s.ExternalSystemInstance != null ? s.ExternalSystemInstance.TypeCode : null,
                s.Case != null ? s.Case.SubjectIdentifier : null,
                s.IdempotencyKey,
                s.Status,
                s.Priority,
                s.SubmittedAt,
                s.LastAttemptAt,
                s.RespondedAt,
                s.ErrorMessage))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new SubmissionPage(rows, total, page, pageSize);
    }

    /// <summary>
    /// Reset a single submission back to <c>pending</c> + clear its
    /// error. Emits an
    /// <c>inspection.icums.submission_requeued</c> domain event tagged
    /// with the actor user id and the submission id. Idempotent: if the
    /// row is already pending, no-op (no event emitted).
    /// </summary>
    public async Task<RequeueResult> RequeueAsync(
        Guid submissionId,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();

        var row = await _db.OutboundSubmissions
            .FirstOrDefaultAsync(s => s.Id == submissionId, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return new RequeueResult(false, 0, "Submission not found in this tenant.");
        }
        if (string.Equals(row.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return new RequeueResult(true, 0, "Already pending — no-op.");
        }

        row.Status = "pending";
        row.ErrorMessage = null;
        row.ResponseJson = null;
        row.RespondedAt = null;
        row.LastAttemptAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var requeuePayload = JsonSerializer.SerializeToElement(new
        {
            submission_id = row.Id,
            case_id = row.CaseId,
            external_system_instance_id = row.ExternalSystemInstanceId,
            actor_user_id = actorUserId,
            bulk = false,
        });
        await _events.PublishAsync(DomainEvent.Create(
            tenantId: _tenant.TenantId,
            actorUserId: actorUserId,
            correlationId: null,
            eventType: "inspection.icums.submission_requeued",
            entityType: "OutboundSubmission",
            entityId: row.Id.ToString(),
            payload: requeuePayload,
            idempotencyKey: $"requeue:{row.Id}:{_clock.GetUtcNow():yyyyMMddHHmmssfff}",
            clock: _clock), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Requeued OutboundSubmission {SubmissionId} (case {CaseId}, instance {InstanceId}) for tenant {TenantId} by {ActorUserId}.",
            row.Id, row.CaseId, row.ExternalSystemInstanceId, _tenant.TenantId, actorUserId);

        return new RequeueResult(true, 1, null);
    }

    /// <summary>
    /// Bulk-requeue every <see cref="OutboundSubmission"/> matching
    /// <paramref name="filters"/>, up to a hard cap of
    /// <see cref="MaxBulkRequeueRows"/> rows. Rows already in
    /// <c>pending</c> are skipped. Emits a single
    /// <c>inspection.icums.submission_bulk_requeued</c> domain event
    /// summarising the action; per-row events are NOT emitted (would
    /// overwhelm the audit log on a 1000-row flip).
    /// </summary>
    public async Task<RequeueResult> RequeueBulkAsync(
        SubmissionQueueFilter filters,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filters);
        EnsureTenantResolved();

        IQueryable<OutboundSubmission> q = _db.OutboundSubmissions;

        if (filters.Statuses is { Count: > 0 } statuses)
        {
            q = q.Where(s => statuses.Contains(s.Status));
        }
        else
        {
            // Safety: never bulk-requeue across all statuses without an
            // explicit list. The page always sends at least one (defaults
            // to "error").
            return new RequeueResult(false, 0, "Bulk requeue requires at least one status filter.");
        }
        if (filters.ExternalSystemInstanceId is Guid esiId)
        {
            q = q.Where(s => s.ExternalSystemInstanceId == esiId);
        }
        if (filters.SubmittedFromUtc is DateTimeOffset from)
        {
            q = q.Where(s => s.SubmittedAt >= from);
        }
        if (filters.SubmittedToUtc is DateTimeOffset to)
        {
            q = q.Where(s => s.SubmittedAt < to);
        }
        if (!string.IsNullOrWhiteSpace(filters.SearchText))
        {
            var needle = filters.SearchText.Trim();
            q = q.Where(s =>
                s.IdempotencyKey.Contains(needle) ||
                (s.Case != null && s.Case.SubjectIdentifier.Contains(needle)));
        }

        // Skip rows already in pending; they shouldn't count against the cap.
        q = q.Where(s => s.Status != "pending");

        var rows = await q
            .OrderByDescending(s => s.Priority)
            .ThenBy(s => s.SubmittedAt)
            .Take(MaxBulkRequeueRows)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return new RequeueResult(true, 0, "No matching non-pending rows.");
        }

        var now = _clock.GetUtcNow();
        foreach (var row in rows)
        {
            row.Status = "pending";
            row.ErrorMessage = null;
            row.ResponseJson = null;
            row.RespondedAt = null;
            row.LastAttemptAt = now;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        var bulkPayload = JsonSerializer.SerializeToElement(new
        {
            row_count = rows.Count,
            actor_user_id = actorUserId,
            status_filter = filters.Statuses,
            external_system_instance_id = filters.ExternalSystemInstanceId,
            search_text = filters.SearchText,
            capped_at_max = rows.Count >= MaxBulkRequeueRows,
        });
        await _events.PublishAsync(DomainEvent.Create(
            tenantId: _tenant.TenantId,
            actorUserId: actorUserId,
            correlationId: null,
            eventType: "inspection.icums.submission_bulk_requeued",
            entityType: "OutboundSubmissionBatch",
            entityId: $"bulk:{now:yyyyMMddHHmmssfff}",
            payload: bulkPayload,
            idempotencyKey: $"requeue:bulk:{actorUserId}:{now:yyyyMMddHHmmssfff}",
            clock: _clock), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Bulk-requeued {Count} OutboundSubmissions for tenant {TenantId} by {ActorUserId}.",
            rows.Count, _tenant.TenantId, actorUserId);

        var notice = rows.Count >= MaxBulkRequeueRows
            ? $"Requeued {rows.Count} rows (hit the {MaxBulkRequeueRows}-row safety cap; refresh + run again for more)."
            : $"Requeued {rows.Count} row(s).";
        return new RequeueResult(true, rows.Count, notice);
    }

    /// <summary>
    /// Fetch the raw <c>PayloadJson</c> + <c>ResponseJson</c> for a
    /// single submission. Used by the queue page's "View payload" modal.
    /// Returns null when the row does not exist in the tenant.
    /// </summary>
    public async Task<SubmissionPayload?> GetPayloadAsync(
        Guid submissionId,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();

        var row = await _db.OutboundSubmissions.AsNoTracking()
            .Where(s => s.Id == submissionId)
            .Select(s => new SubmissionPayload(
                s.Id,
                s.IdempotencyKey,
                s.Status,
                s.PayloadJson,
                s.ResponseJson,
                s.ErrorMessage,
                s.SubmittedAt,
                s.LastAttemptAt,
                s.RespondedAt))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row;
    }

    /// <summary>
    /// Aggregate counts grouped by <see cref="OutboundSubmission.Status"/>
    /// for the dashboard summary card. RLS-narrowed.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(
        Guid? externalSystemInstanceId = null,
        CancellationToken ct = default)
    {
        EnsureTenantResolved();
        IQueryable<OutboundSubmission> q = _db.OutboundSubmissions.AsNoTracking();
        if (externalSystemInstanceId is Guid esiId)
        {
            q = q.Where(s => s.ExternalSystemInstanceId == esiId);
        }
        var grouped = await q
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return grouped.ToDictionary(x => x.Status, x => x.Count, StringComparer.OrdinalIgnoreCase);
    }

    private void EnsureTenantResolved()
    {
        if (!_tenant.IsResolved)
            throw new InvalidOperationException(
                "ITenantContext is not resolved; IcumsSubmissionQueueAdminService must run inside a tenant-aware request scope.");
    }
}

/// <summary>
/// Filter for <see cref="IcumsSubmissionQueueAdminService.ListAsync"/>
/// and <see cref="IcumsSubmissionQueueAdminService.RequeueBulkAsync"/>.
/// All fields optional except where noted on the consuming method.
/// </summary>
public sealed record SubmissionQueueFilter
{
    /// <summary>Statuses to include (multi-select). Null/empty = all.</summary>
    public IReadOnlyList<string>? Statuses { get; init; }

    /// <summary>Filter by a specific external system instance. Null = all.</summary>
    public Guid? ExternalSystemInstanceId { get; init; }

    /// <summary>Inclusive lower bound on <c>SubmittedAt</c>.</summary>
    public DateTimeOffset? SubmittedFromUtc { get; init; }

    /// <summary>Exclusive upper bound on <c>SubmittedAt</c>.</summary>
    public DateTimeOffset? SubmittedToUtc { get; init; }

    /// <summary>Substring search across idempotency key and case subject identifier.</summary>
    public string? SearchText { get; init; }
}

/// <summary>One row in the submission-queue list.</summary>
public sealed record SubmissionRow(
    Guid Id,
    Guid CaseId,
    Guid ExternalSystemInstanceId,
    string? ExternalSystemDisplayName,
    string? ExternalSystemTypeCode,
    string? CaseSubjectIdentifier,
    string IdempotencyKey,
    string Status,
    int Priority,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? RespondedAt,
    string? ErrorMessage);

/// <summary>Paged list result.</summary>
public sealed record SubmissionPage(
    IReadOnlyList<SubmissionRow> Rows,
    int TotalCount,
    int Page,
    int PageSize);

/// <summary>Payload-fetch result.</summary>
public sealed record SubmissionPayload(
    Guid Id,
    string IdempotencyKey,
    string Status,
    string PayloadJson,
    string? ResponseJson,
    string? ErrorMessage,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? RespondedAt);

/// <summary>Outcome of a (single or bulk) requeue call.</summary>
/// <param name="Success">True iff the call ran (does not imply rows mutated).</param>
/// <param name="RowsAffected">How many rows were flipped to pending.</param>
/// <param name="Notice">Operator-facing notice to render on the page.</param>
public sealed record RequeueResult(
    bool Success,
    int RowsAffected,
    string? Notice);
