using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NickERP.Platform.Telemetry;

namespace NickERP.Inspection.Web.Endpoints;

/// <summary>
/// Sprint 9 / FU-host-status — <c>/healthz/workers</c> aggregator.
///
/// <para>
/// Each long-running <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// in this host implements <see cref="IBackgroundServiceProbe"/> and is
/// registered as a singleton in DI. This endpoint resolves
/// <c>IEnumerable&lt;IBackgroundServiceProbe&gt;</c>, snapshots each, and
/// returns a JSON document with per-worker liveness plus an aggregate
/// verdict. Runbook 03 (PreRender stalled) points at this endpoint
/// instead of log-grepping for tick lines.
/// </para>
///
/// <para>
/// Auth: <c>RequireAuthorization()</c> — the endpoint exposes
/// operational telemetry (last error messages, tick counts) that we
/// don't want anonymous callers reading. The <c>/healthz/live</c> +
/// <c>/healthz/ready</c> endpoints stay anonymous for kubelet probes
/// because they don't carry payload data; this one does.
/// </para>
///
/// <para>
/// Always returns <c>200 OK</c> regardless of any worker's health —
/// the body carries the verdict, and a 200 means the endpoint itself
/// is functioning. Future enhancement: <c>?strict=true</c> could
/// surface 503 when any probe is Unhealthy so a load balancer can
/// route around the host. Out of scope for FU-host-status — TODO if
/// we ever wire this into LB health.
/// </para>
/// </summary>
public static class WorkersHealthzEndpoint
{
    /// <summary>
    /// Map <c>GET /healthz/workers</c>. Wire after
    /// <c>UseAuthentication()</c> + <c>UseAuthorization()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapWorkersHealthzEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz/workers", GetAsync).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Aggregate every registered <see cref="IBackgroundServiceProbe"/>'s
    /// state into a single JSON response. Public for unit-testing —
    /// production callers reach this via the routing table above.
    /// </summary>
    public static IResult GetAsync(HttpContext http)
    {
        var probes = http.RequestServices.GetServices<IBackgroundServiceProbe>().ToList();

        var workers = probes
            .Select(p =>
            {
                var s = p.GetState();
                return new WorkerHealthDto(
                    Name: p.WorkerName,
                    Health: s.Health.ToString(),
                    LastTickAt: s.LastTickAt,
                    LastSuccessAt: s.LastSuccessAt,
                    TickCount: s.TickCount,
                    ErrorCount: s.ErrorCount,
                    LastError: s.LastError,
                    LastErrorAt: s.LastErrorAt);
            })
            .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var overall = AggregateOverall(workers);

        return Results.Ok(new WorkersHealthDto(Overall: overall, Workers: workers));
    }

    /// <summary>
    /// Aggregate per-worker verdicts into a single overall verdict.
    /// Any Unhealthy → Unhealthy; else any Degraded → Degraded; else
    /// Healthy. Empty probe set → Healthy (no workers, nothing to be
    /// wedged about).
    /// </summary>
    internal static string AggregateOverall(IEnumerable<WorkerHealthDto> workers)
    {
        var states = workers.Select(w => w.Health).ToList();
        if (states.Count == 0) return nameof(BackgroundServiceHealth.Healthy);
        if (states.Any(s => s == nameof(BackgroundServiceHealth.Unhealthy)))
            return nameof(BackgroundServiceHealth.Unhealthy);
        if (states.Any(s => s == nameof(BackgroundServiceHealth.Degraded)))
            return nameof(BackgroundServiceHealth.Degraded);
        return nameof(BackgroundServiceHealth.Healthy);
    }
}

/// <summary>One worker's health snapshot in the <c>/healthz/workers</c> JSON.</summary>
public sealed record WorkerHealthDto(
    string Name,
    string Health,
    DateTimeOffset? LastTickAt,
    DateTimeOffset? LastSuccessAt,
    long TickCount,
    long ErrorCount,
    string? LastError,
    DateTimeOffset? LastErrorAt);

/// <summary>Top-level <c>/healthz/workers</c> response shape.</summary>
public sealed record WorkersHealthDto(
    string Overall,
    IReadOnlyList<WorkerHealthDto> Workers);
