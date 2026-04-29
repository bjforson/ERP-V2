using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NickERP.EdgeNode;

// ---------------------------------------------------------------------------
// Sprint 11 / P2 — edge-node host. Worker + small Kestrel surface for
// /edge/healthz. Runs on the edge box (port-of-entry inspection lane,
// remote NickFinance branch, scanner-attached field node) where the
// central server is intermittently reachable.
// ---------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Bind config sections. The shapes are intentionally split:
//   - EdgeNode:Id / Token / ReplayIntervalSeconds / MaxBatchSize → EdgeNodeOptions
//   - Server:Url → EdgeServerOptions
// so a redeploy that re-targets a different server doesn't accidentally
// drop the edge identity.
// ---------------------------------------------------------------------------
builder.Services
    .AddOptions<EdgeNodeOptions>()
    .Bind(builder.Configuration.GetSection("EdgeNode"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Id),
        "EdgeNode:Id must be configured (a stable identifier for this edge box).")
    .Validate(o => o.ReplayIntervalSeconds > 0,
        "EdgeNode:ReplayIntervalSeconds must be > 0.")
    .Validate(o => o.MaxBatchSize > 0,
        "EdgeNode:MaxBatchSize must be > 0.");

builder.Services
    .AddOptions<EdgeServerOptions>()
    .Bind(builder.Configuration.GetSection("Server"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Url),
        "Server:Url must be configured (the central NickERP base URL).");

// ---------------------------------------------------------------------------
// SQLite buffer DB. Schema initialisation is intentionally light: in dev
// EnsureCreated() bootstraps the file; in real deploys the DDL script
// (`tools/edge-sqlite/edge-outbox-schema.sql` — generated via
// `dotnet ef migrations script --idempotent`) seeds the file before the
// host boots.
// ---------------------------------------------------------------------------
var bufferConn = builder.Configuration.GetConnectionString("EdgeBuffer")
    ?? "Data Source=edge-outbox.db";
builder.Services.AddDbContext<EdgeBufferDbContext>(opts =>
{
    opts.UseSqlite(bufferConn);
});

// HTTP client for the replay path. Single named client; re-resolved
// per call so token / timeout knobs can be tweaked without restart.
builder.Services.AddHttpClient(EdgeReplayClient.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddScoped<IEdgeEventCapture, EdgeEventCapture>();
builder.Services.AddSingleton<IEdgeReplayClient, EdgeReplayClient>();

// Replay worker — singleton so the probe interface (resolved by the
// /edge/healthz endpoint) returns the live worker's state. AddHostedService<T>
// alone would create a separate instance and the probe would always look
// "never ticked" (same gotcha the inspection workers hit in Sprint 9 /
// FU-host-status; replicated pattern).
builder.Services.AddSingleton<EdgeReplayWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EdgeReplayWorker>());
builder.Services.AddSingleton<IEdgeReplayProbe>(sp => sp.GetRequiredService<EdgeReplayWorker>());

var app = builder.Build();

// ---------------------------------------------------------------------------
// Bootstrap the local SQLite buffer if this is a fresh edge install. In
// dev EnsureCreated is fine; in prod the DDL script is a separate
// operator-driven step (so the SQLite file can be pre-positioned with
// the right ownership / security ACLs).
// ---------------------------------------------------------------------------
{
    var ensureCreate = app.Configuration.GetValue<bool?>("EdgeNode:EnsureBufferCreated")
        ?? app.Environment.IsDevelopment();
    if (ensureCreate)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeBufferDbContext>();
        await db.Database.EnsureCreatedAsync();
        var bootLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Startup.EdgeBuffer");
        bootLogger.LogInformation("Edge buffer ensured at {Conn}.", bufferConn);
    }
}

// ---------------------------------------------------------------------------
// /edge/healthz — operational visibility for an ops dashboard or remote
// monitoring. Returns the configured edge node id, the current queue
// depth (unreplayed entries), and the last-successful-replay timestamp.
// Anonymous endpoint — the body is operational metadata, no PII; the
// edge host is on a private network and behind firewall on real
// deploys.
// ---------------------------------------------------------------------------
app.MapGet("/edge/healthz", (IEdgeReplayProbe probe) =>
{
    return Results.Ok(new EdgeHealthzResponse(
        EdgeNodeId: probe.EdgeNodeId,
        QueueDepth: probe.QueueDepth,
        LastSuccessfulReplayAt: probe.LastSuccessfulReplayAt));
}).AllowAnonymous();

app.Run();

/// <summary>Shape of the GET <c>/edge/healthz</c> response body.</summary>
public sealed record EdgeHealthzResponse(
    string EdgeNodeId,
    long QueueDepth,
    DateTimeOffset? LastSuccessfulReplayAt);

/// <summary>
/// Expose the auto-generated <c>Program</c> entry-point type so the
/// edge-node test project can spin up the host via
/// <c>WebApplicationFactory&lt;Program&gt;</c>. Mirrors the Inspection.Web
/// pattern.
/// </summary>
public partial class Program;
