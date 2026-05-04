using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NickERP.Inspection.Core.Entities;
using NickERP.Inspection.Database;
using NickERP.Platform.Audit.Events;
using NickERP.Platform.Tenancy;

namespace NickERP.Inspection.Web.Services;

/// <summary>
/// Sprint 20 / B1.2 — analyst annotation persistence. The image viewer
/// lets the operator draw rectangles on a scan, attach a note + severity,
/// and persist the result as a <see cref="Finding"/> on a (possibly
/// freshly-created) <see cref="AnalystReview"/>.
///
/// <para>
/// Why this lives in its own service instead of <see cref="CaseWorkflowService"/>:
/// annotation is a narrow, idempotent storage concern that does not move
/// the case state forward. Bundling it with the workflow surface would
/// blur the responsibility — workflow advances state, annotation only
/// captures evidence. Keeping them separate also lets the viewer page
/// inject just this service without inheriting the workflow's heavier
/// dependency graph.
/// </para>
///
/// <para>
/// Findings are anchored to an <see cref="AnalystReview"/>, which itself
/// is anchored to a <see cref="ReviewSession"/>. If the analyst hasn't
/// formally clicked "Assign me + start review" before annotating,
/// <see cref="EnsureReviewAsync"/> creates a session + an in-progress
/// <c>AnalystReview</c> with placeholder telemetry — so analyst notes
/// never get lost. The verdict-set path on <c>CaseWorkflowService</c>
/// continues to create its own AnalystReview row (the verdict's review
/// captures the time-to-decision telemetry); annotation reviews are
/// distinct rows so the two responsibilities don't fight over the
/// same state.
/// </para>
/// </summary>
public sealed class AnalystAnnotationService
{
    public const string AnnotationFindingType = "analyst.annotation";

    private readonly InspectionDbContext _db;
    private readonly IEventPublisher _events;
    private readonly ITenantContext _tenant;
    private readonly AuthenticationStateProvider _auth;
    private readonly ILogger<AnalystAnnotationService> _logger;

    public AnalystAnnotationService(
        InspectionDbContext db,
        IEventPublisher events,
        ITenantContext tenant,
        AuthenticationStateProvider auth,
        ILogger<AnalystAnnotationService> logger)
    {
        _db = db;
        _events = events;
        _tenant = tenant;
        _auth = auth;
        _logger = logger;
    }

    /// <summary>
    /// Record a rectangular annotation on a scan artifact. The rectangle
    /// is in image-pixel space (origin top-left) and gets persisted in
    /// <see cref="Finding.LocationInImageJson"/> as
    /// <c>{"x":...,"y":...,"w":...,"h":...,"artifactId":"..."}</c>.
    /// </summary>
    /// <param name="caseId">Owning case.</param>
    /// <param name="scanArtifactId">The artifact the analyst drew on. Stored in the location payload so the rules-and-findings tab can replay it.</param>
    /// <param name="x">Top-left x in image pixels.</param>
    /// <param name="y">Top-left y in image pixels.</param>
    /// <param name="w">Width in image pixels.</param>
    /// <param name="h">Height in image pixels.</param>
    /// <param name="severity">"info" / "warning" / "critical". Anything else is normalised to "info".</param>
    /// <param name="note">Free-text annotation. Optional.</param>
    /// <returns>The persisted <see cref="Finding"/> row.</returns>
    public async Task<Finding> AddAnnotationAsync(
        Guid caseId,
        Guid scanArtifactId,
        int x, int y, int w, int h,
        string severity,
        string? note,
        CancellationToken ct = default)
    {
        if (w <= 0 || h <= 0)
        {
            throw new ArgumentException(
                $"Annotation rectangle must have positive dimensions; got w={w}, h={h}.",
                nameof(w));
        }

        var (actor, tenantId) = await CurrentActorAsync();
        if (actor is null)
        {
            // Annotations are an analyst affordance — anonymous principals
            // shouldn't be persisting evidence. Surface the error so the
            // caller can render the auth-required message.
            throw new InvalidOperationException("Cannot record annotation without an authenticated analyst.");
        }

        var review = await EnsureReviewAsync(caseId, actor.Value, tenantId, ct);

        var locationJson = JsonSerializer.Serialize(new
        {
            x, y, w, h,
            artifactId = scanArtifactId.ToString(),
        });

        var finding = new Finding
        {
            AnalystReviewId = review.Id,
            FindingType = AnnotationFindingType,
            Severity = NormaliseSeverity(severity),
            LocationInImageJson = locationJson,
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
            TenantId = tenantId,
        };
        _db.Findings.Add(finding);
        await _db.SaveChangesAsync(ct);

        try
        {
            var evt = NickERP.Platform.Audit.Events.DomainEvent.Create(
                tenantId, actor, correlationId: null,
                eventType: "nickerp.inspection.annotation_added",
                entityType: "Finding",
                entityId: finding.Id.ToString(),
                payload: JsonSerializer.SerializeToElement(new
                {
                    finding.Id,
                    CaseId = caseId,
                    ScanArtifactId = scanArtifactId,
                    finding.Severity,
                    finding.FindingType,
                }),
                idempotencyKey: $"annotation-{finding.Id}");
            await _events.PublishAsync(evt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to publish annotation_added event for finding {FindingId}.",
                finding.Id);
        }

        return finding;
    }

    /// <summary>
    /// Drop an annotation. Only the analyst who created the finding can
    /// delete it (admin override is intentionally not wired here — it's
    /// a follow-up if the requirement surfaces). Returns
    /// <c>true</c> when the row was removed, <c>false</c> when the row
    /// did not exist or belonged to someone else.
    /// </summary>
    public async Task<bool> DeleteAnnotationAsync(Guid findingId, CancellationToken ct = default)
    {
        var (actor, tenantId) = await CurrentActorAsync();
        if (actor is null) return false;

        var f = await _db.Findings
            .Where(x => x.Id == findingId && x.FindingType == AnnotationFindingType)
            .Include(x => x.Review)
            .FirstOrDefaultAsync(ct);
        if (f is null) return false;

        // Owner check: walk Finding → AnalystReview → ReviewSession to
        // get the AnalystUserId. Tenant filter is a sanity check; RLS
        // already isolates rows but a belt-and-suspenders compare
        // protects against bugs in tenant resolution.
        if (f.TenantId != tenantId) return false;
        if (f.Review is null) return false;
        var session = await _db.ReviewSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == f.Review.ReviewSessionId, ct);
        if (session is null || session.AnalystUserId != actor.Value) return false;

        _db.Findings.Remove(f);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// List the persisted annotations for a scan artifact, newest-first.
    /// Used by the viewer page to re-overlay rectangles on initial load.
    /// </summary>
    public async Task<List<AnnotationView>> ListForArtifactAsync(Guid scanArtifactId, CancellationToken ct = default)
    {
        // Walk Finding → AnalystReview → ReviewSession; the viewer is
        // strictly per-scan-artifact so we filter the location payload
        // string for the artifact id (cheap; the payload is small jsonb).
        var idNeedle = scanArtifactId.ToString();
        var rows = await _db.Findings.AsNoTracking()
            .Where(f => f.FindingType == AnnotationFindingType
                        && f.LocationInImageJson.Contains(idNeedle))
            .OrderByDescending(f => f.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        var views = new List<AnnotationView>(rows.Count);
        foreach (var r in rows)
        {
            if (TryParseLocation(r.LocationInImageJson, out var rect, out var artifactInPayload))
            {
                if (artifactInPayload != scanArtifactId) continue;   // stray match in jsonb text — filter out
                views.Add(new AnnotationView(
                    r.Id, scanArtifactId, rect.X, rect.Y, rect.W, rect.H,
                    r.Severity, r.Note, r.CreatedAt));
            }
        }
        return views;
    }

    private async Task<AnalystReview> EnsureReviewAsync(Guid caseId, Guid actorUserId, long tenantId, CancellationToken ct)
    {
        var session = await _db.ReviewSessions
            .Where(s => s.CaseId == caseId && s.AnalystUserId == actorUserId && s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        var now = DateTimeOffset.UtcNow;
        if (session is null)
        {
            session = new ReviewSession
            {
                CaseId = caseId,
                AnalystUserId = actorUserId,
                StartedAt = now,
                Outcome = "in-progress",
                TenantId = tenantId,
            };
            _db.ReviewSessions.Add(session);
            await _db.SaveChangesAsync(ct);    // need the id for the AnalystReview FK
        }

        // Re-use the latest in-progress review for this session if there
        // is one, so a flurry of annotations during the same review
        // ride on a single AnalystReview row. The verdict-set path
        // creates its own row with the time-to-decision telemetry; we
        // intentionally don't share that row.
        var review = await _db.AnalystReviews
            .Where(r => r.ReviewSessionId == session.Id)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (review is null)
        {
            review = new AnalystReview
            {
                ReviewSessionId = session.Id,
                ConfidenceScore = 0.0,            // placeholder; SetVerdict overwrites at decision time
                CreatedAt = now,
                TenantId = tenantId,
            };
            _db.AnalystReviews.Add(review);
            await _db.SaveChangesAsync(ct);
        }

        return review;
    }

    private async Task<(Guid? UserId, long TenantId)> CurrentActorAsync()
    {
        Guid? id = null;
        try
        {
            var state = await _auth.GetAuthenticationStateAsync();
            var idClaim = state.User?.FindFirst("nickerp:id")?.Value;
            if (Guid.TryParse(idClaim, out var g)) id = g;
        }
        catch (InvalidOperationException)
        {
            id = null;
        }
        if (!_tenant.IsResolved)
        {
            throw new InvalidOperationException(
                "Tenant context is not resolved when persisting an analyst annotation.");
        }
        return (id, _tenant.TenantId);
    }

    private static string NormaliseSeverity(string? raw) => (raw ?? string.Empty).ToLowerInvariant() switch
    {
        "warning" => "warning",
        "critical" => "critical",
        _ => "info",
    };

    private static bool TryParseLocation(string raw, out (int X, int Y, int W, int H) rect, out Guid artifactId)
    {
        rect = default;
        artifactId = Guid.Empty;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("x", out var xEl)
                && root.TryGetProperty("y", out var yEl)
                && root.TryGetProperty("w", out var wEl)
                && root.TryGetProperty("h", out var hEl))
            {
                rect = (xEl.GetInt32(), yEl.GetInt32(), wEl.GetInt32(), hEl.GetInt32());
                if (root.TryGetProperty("artifactId", out var aEl)
                    && Guid.TryParse(aEl.GetString(), out var aid))
                {
                    artifactId = aid;
                }
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>
    /// View record returned by <see cref="ListForArtifactAsync"/>. Flat
    /// shape so the Razor page can hand it directly to JS interop without
    /// further translation.
    /// </summary>
    public sealed record AnnotationView(
        Guid Id,
        Guid ArtifactId,
        int X, int Y, int W, int H,
        string Severity,
        string? Note,
        DateTimeOffset CreatedAt);
}
