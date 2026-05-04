using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NickERP.Inspection.Web.Services;

namespace NickERP.Inspection.Web.Endpoints;

/// <summary>
/// Sprint 20 / B1.2 — JSON endpoints backing the analyst viewer's
/// annotation tool. Three operations:
///
/// <list type="bullet">
///   <item><description><c>GET  /api/inspection/annotations?artifactId=...</c> — list annotations for a scan artifact.</description></item>
///   <item><description><c>POST /api/inspection/annotations</c> — create one. Body: <see cref="CreateAnnotationRequest"/>.</description></item>
///   <item><description><c>DELETE /api/inspection/annotations/{id:guid}</c> — drop one (owner only).</description></item>
/// </list>
///
/// All three require auth + a resolved tenant; the underlying service
/// throws if the principal can't be projected to a user id. RLS on
/// <c>inspection.findings</c> enforces tenant isolation at the DB layer.
/// </summary>
public static class AnnotationsEndpoints
{
    public static IEndpointRouteBuilder MapAnnotationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/inspection/annotations").RequireAuthorization();

        group.MapGet("", async (
            Guid artifactId,
            AnalystAnnotationService svc,
            CancellationToken ct) =>
        {
            if (artifactId == Guid.Empty) return Results.BadRequest("artifactId is required");
            var rows = await svc.ListForArtifactAsync(artifactId, ct);
            return Results.Ok(rows);
        });

        group.MapPost("", async (
            CreateAnnotationRequest req,
            AnalystAnnotationService svc,
            CancellationToken ct) =>
        {
            if (req.CaseId == Guid.Empty || req.ScanArtifactId == Guid.Empty)
                return Results.BadRequest("caseId and scanArtifactId are required");
            try
            {
                var f = await svc.AddAnnotationAsync(
                    req.CaseId, req.ScanArtifactId,
                    req.X, req.Y, req.W, req.H,
                    req.Severity ?? "info", req.Note, ct);
                return Results.Ok(new { f.Id, f.CreatedAt });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        group.MapDelete("{id:guid}", async (
            Guid id,
            AnalystAnnotationService svc,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteAnnotationAsync(id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }

    /// <summary>POST body for creating an annotation. Coordinates are in image-pixel space.</summary>
    public sealed record CreateAnnotationRequest(
        Guid CaseId,
        Guid ScanArtifactId,
        int X, int Y, int W, int H,
        string? Severity,
        string? Note);
}
